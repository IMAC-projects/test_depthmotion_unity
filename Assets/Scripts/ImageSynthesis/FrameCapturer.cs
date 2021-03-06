using UnityEngine;
using UnityEngine.Rendering;
using System.Collections;
using System.Collections.Generic;

// @TODO:
// . support custom color wheels in optical flow via lookup textures
// . support custom depth encoding
// . support multiple overlay cameras
// . tests
// . better example scene(s)

// @KNOWN ISSUES
// . Motion Vectors can produce incorrect results in Unity 5.5.f3 when
//      1) during the first rendering frame
//      2) rendering several cameras with different aspect ratios - vectors do stretch to the sides of the screen

namespace ImageSynthesis 
{
	[RequireComponent (typeof(Camera))]
	public class FrameCapturer : MonoBehaviour
	{

		#region Serialised parameters

		[SerializeField] Vector2Int dim = new Vector2Int(1920, 1080);
        // todo: this one should be a power of two, create an attribute for it.
        [RangeAttribute(1, 8)]
		[SerializeField] int downscalingFactor = 1;
        
        [RangeAttribute(1, 500)]
		[SerializeField] int samplingStep = 1;

		[SerializeField] PassKind[] targets;

		[SerializeField] Shader uberReplacementShader;
		[SerializeField] Shader opticalFlowShader;

		[SerializeField] float opticalFlowSensitivity = 1.0f;
		[SerializeField] int frameRate = 30;
		
		#endregion

		#region Public structs 

		class CapturePass
		{
			// configuration
			public PassKind kind;
			public string name;
			
			public bool needsRescale;
			public bool supportsAntiAliasing;

			public byte[] buffer;
			public int dataLength;

			public RenderTexture renderTexture;
			public Texture2D texture;

			public CapturePass(PassKind kind_, string name_, bool supportsAntiAliasing_)
			{
				kind = kind_;
				name = name_;
				
				buffer = new byte[0];
				dataLength = buffer.Length;
				
				needsRescale = false;
				supportsAntiAliasing = supportsAntiAliasing_;
				
				camera = null;

				renderTexture = null;
				texture = null;
			}

			~CapturePass()
			{
				if (renderTexture != null)
				{
					RenderTexture.ReleaseTemporary(renderTexture);
				}
			}

			public void CreateTextures(int width, int height, bool needsRescale_)
			{ 
				renderTexture = RenderTexture.GetTemporary(
					width, 
					height, 
					c_depthSize, 
					RenderTextureFormat.Default, 
					RenderTextureReadWrite.Default
	            );
				
				texture = new Texture2D(width, height, TextureFormat.RGB24, false);

				needsRescale = needsRescale_;
				
				var antiAliasing = (supportsAntiAliasing) ? Mathf.Max(1, QualitySettings.antiAliasing) : 1;

				renderTexture.antiAliasing = antiAliasing;
			}
			
			// impl
			public Camera camera;
		};
		
		public enum PassKind
		{
			EImage,
			EId,
			ELayer,
			EDepth,
			ENormals,
			EFlow
		}
		#endregion

		#region Unity life-cycle and events 

		void Awake()
		{
			m_cam = GetComponent<Camera>();
			// Set the non-downscaled screen resolution as requested.
			Screen.SetResolution(dim.x, dim.y, Screen.fullScreenMode);
			
            m_dim = dim / downscalingFactor;
			
			m_capturePasses = new CapturePass[targets.Length];
			// use real camera to capture final image
			for (int q = 0; q < m_capturePasses.Length; ++q)
			{
				m_capturePasses[q] = s_allCapturePasses[targets[q]];
				m_capturePasses[q].camera = CreateHiddenCamera (m_capturePasses[q].name);
				bool needsRecale = m_capturePasses[q].kind == PassKind.EFlow;
				m_capturePasses[q].CreateTextures(m_dim.x, m_dim.y, needsRecale);
			}
			
            m_fullRenderTexture = RenderTexture.GetTemporary(
					m_dim.x, 
					m_dim.y, 
					c_depthSize, 
					RenderTextureFormat.Default, 
					RenderTextureReadWrite.Default
	            );
			
            m_saver = new Util.SnapshotSaver();
            m_saver.CreateFileTree();

            // Important:
            // The motion vectors need the previous frame in order to be computed.
            // As such, they should be renderer two consecutive frames..
            // If we save each frame, there won't be an issue.
            // But that's not what we're doing here.
            
            m_shouldUseQuickFix = samplingStep > 1;
            
		}

		void OnDestroy()
		{
			RenderTexture.ReleaseTemporary(m_fullRenderTexture);
			
			RenderPipelineManager.endCameraRendering -= OnEndCameraRendering;	
		}

		void Start()
		{
			Time.captureFramerate = frameRate;

			// default fallbacks, if shaders are unspecified
			if (!uberReplacementShader)
			{
				uberReplacementShader = Shader.Find("Hidden/UberReplacement");
			}

			if (!opticalFlowShader)
			{
				opticalFlowShader = Shader.Find("Hidden/OpticalFlow");
				
			}

			OnCameraChange();
			OnSceneChange();
			
			RenderPipelineManager.endCameraRendering += OnEndCameraRendering;	
		}

		void LateUpdate()
		{
			#if UNITY_EDITOR
			if (DetectPotentialSceneChangeInEditor())
			{
				OnSceneChange();
			}
			#endif // UNITY_EDITOR

			// @TODO: detect if camera properties actually changed
			OnCameraChange();
		}
		
        // OnPostRender only works with the legacy render pipeline
        void OnPostRender()
        {
            SaveFrameStep();
        }
        

        // Kind of an equivalent for SRP.
        void OnEndCameraRendering(ScriptableRenderContext context, Camera cam)
        {
            if (cam == m_cam)
            {
                SaveFrameStep();
            }
        }
        

		void OnCameraChange()
		{
			int targetDisplay = 1;
			var mainCamera = m_cam;
			foreach (var pass in m_capturePasses)
			{
				if (pass.camera == mainCamera)
					continue;

				// cleanup capturing camera
				pass.camera.RemoveAllCommandBuffers();

				// copy all "main" camera parameters into capturing camera
				pass.camera.CopyFrom(mainCamera);

				// set targetDisplay here since it gets overriden by CopyFrom()
				pass.camera.targetDisplay = targetDisplay++;
			}

			// cache materials and setup material properties
			if (!m_opticalFlowMaterial || m_opticalFlowMaterial.shader != opticalFlowShader)
			{
				m_opticalFlowMaterial = new Material(opticalFlowShader);
			}
			m_opticalFlowMaterial.SetFloat("_Sensitivity", opticalFlowSensitivity);

			// setup command buffers and replacement shaders
			foreach (var pass in m_capturePasses)
			{
				SetupCapturePassCamera(pass);
			}
		}


		void OnSceneChange()
		{
			var renderers = FindObjectsOfType<Renderer>();
			var mpb = new MaterialPropertyBlock();
			foreach (var r in renderers)
			{
				var id = r.gameObject.GetInstanceID();
				var layer = r.gameObject.layer;
				var tag = r.gameObject.tag;

				mpb.SetColor("_ObjectColor", ColorEncoding.EncodeIDAsColor(id));
				mpb.SetColor("_CategoryColor", ColorEncoding.EncodeLayerAsColor(layer));
				r.SetPropertyBlock(mpb);
			}
		}
		
        #endregion
		
		#region Capture and saving
        void SaveFrameStep()
        {
            if (ShouldCaptureCurrentFrame())
            {
	            print(m_currentFrameIndex);
                SaveFrame();
            }
            ++m_currentFrameIndex;
        }
        
		public void SaveCapturePass(PassKind kind, int frameIndex)
		{
			// get capture pass of kind
			if (!System.Array.Exists(m_capturePasses, pass => pass.kind == kind))
			{
				throw new System.ArgumentException("Pass of type " + kind + "was not initialised for this Capture.");
			}

			var capturePass = System.Array.Find(m_capturePasses, pass => pass.kind == kind);

			// Wait for the end of frame
			StartCoroutine(
				WaitForEndOfFrameAndCacheCapturePass(capturePass, frameIndex)
			);
		}

		IEnumerator WaitForEndOfFrameAndCacheCapturePass(CapturePass capturePass, int frameIndex)
		{
			yield return new WaitForEndOfFrame();
			SaveCapturePass(capturePass, frameIndex);
		}
		
        void SaveFrame(int frameIndex)
        {
            foreach (var kind in targets)
            {
                // get and save the data 
               SaveCapturePass(kind, frameIndex);
            }
        }
		
        void SaveFrame()
        {
            // Important:
            // The engine seems to not render the Motion Vectors at all
            // if we do nothing with them (that is, not save them.)
            // And we need the previous frame to be able to compute the current.
            // As such, will save a couple each time,
            m_rectifiedFrameIndex = m_currentFrameIndex;
            if (m_shouldUseQuickFix && (m_rectifiedFrameIndex + 1) % samplingStep == 0)
            {
                // Quick fix to force sampling of previous frame
                // Then we can overwrite it on next frame.
                m_rectifiedFrameIndex += 1;
            }
            else if (m_numberCaptured == 0)
            {
                // Also, the very first frame is always bad (cause it does have a previous frame to be computed from),
                // so we always use the fix on it.
                // It will be overwritten by the next frame to be sampled.
                m_rectifiedFrameIndex += samplingStep;
            }
            SaveFrame(m_rectifiedFrameIndex);
        }
        
		
		void SaveCapturePass(CapturePass capturePass, int frameIndex)
		{
			ComputePixelData(capturePass);
			m_saver.Save(frameIndex, capturePass.kind, capturePass.buffer);

			++m_numberCaptured;
		}
			
		void ComputePixelData(CapturePass capturePass)
		{
			var renderTexture = capturePass.needsRescale
				? m_fullRenderTexture
				: capturePass.renderTexture;

			var cam = capturePass.camera;
			var texture = capturePass.texture;

			
			var prevActiveRT = RenderTexture.active;
			var prevCameraRT = cam.targetTexture;

			// render to offscreen texture (readonly from CPU side)
			RenderTexture.active = renderTexture;
			cam.targetTexture = renderTexture;

			cam.Render();

			if (capturePass.needsRescale)
			{
				// blit to rescale (see issue with Motion Vectors in @KNOWN ISSUES)
				RenderTexture.active = capturePass.renderTexture;
				Graphics.Blit(renderTexture, capturePass.renderTexture);
			}

			// read offscreen texture contents into the CPU readable texture
			
			texture.ReadPixels(new Rect(0, 0, texture.width, texture.height), 0, 0);
			texture.Apply();

			// encode texture into PNG
			capturePass.buffer = texture.EncodeToPNG();

			// restore state and cleanup
			cam.targetTexture = prevCameraRT;
			RenderTexture.active = prevActiveRT;
		}

		#endregion
		
		#region Private utility

		bool ShouldCaptureCurrentFrame()
		{
            // We save at least a couple of those.
            // So that they are both computed and the second gives the actual result.
            // The first motion vector texture is discarded (if it is faulty, with the quick fix).
			return m_currentFrameIndex % samplingStep == 0
			       || (m_currentFrameIndex + 1) % samplingStep == 0;
		}

		Camera CreateHiddenCamera(string name)
		{
			var go = new GameObject (name, typeof (Camera));
			go.hideFlags = HideFlags.HideAndDontSave;
			go.transform.parent = transform;

			var newCamera = go.GetComponent<Camera>();
			return newCamera;
		}

		void SetupCapturePassCamera(CapturePass pass)
		{
			switch (pass.kind)
			{
			case PassKind.EId:
				SetupCameraWithReplacementShader(
					pass.camera, 
					uberReplacementShader, 
					ReplacementModes.ObjectId
					);
				break;
			case PassKind.ELayer:
				SetupCameraWithReplacementShader(
					pass.camera, 
					uberReplacementShader, 
					ReplacementModes.CatergoryId
					);
				 break;
			case PassKind.EDepth:
				SetupCameraWithReplacementShader(
					pass.camera, 
					uberReplacementShader, 
					ReplacementModes.DepthCompressed, 
					Color.white
					);
				break;
			case PassKind.ENormals:
				SetupCameraWithReplacementShader(
					pass.camera, 
					uberReplacementShader, 
					ReplacementModes.Normals
					);
				break;
			case PassKind.EFlow:
				SetupCameraWithPostShader(
					pass.camera, 
					m_opticalFlowMaterial, 
					DepthTextureMode.Depth | DepthTextureMode.MotionVectors
					);
				break;
			}
		}
		static void SetupCameraWithReplacementShader(Camera cam, Shader shader, ReplacementModes mode)
		{
			SetupCameraWithReplacementShader(cam, shader, mode, Color.black);
		}

		static void SetupCameraWithReplacementShader(Camera cam, Shader shader, ReplacementModes mode, Color clearColor)
		{
			var cb = new CommandBuffer();
			cb.SetGlobalFloat("_OutputMode", (int)mode); // @TODO: CommandBuffer is missing SetGlobalInt() method
			cam.AddCommandBuffer(CameraEvent.BeforeForwardOpaque, cb);
			cam.AddCommandBuffer(CameraEvent.BeforeFinalPass, cb);
			cam.SetReplacementShader(shader, "");
			cam.backgroundColor = clearColor;
			cam.clearFlags = CameraClearFlags.SolidColor;
		}

		static void SetupCameraWithPostShader(Camera cam, Material material, DepthTextureMode depthTextureMode = DepthTextureMode.None)
		{
			var cb = new CommandBuffer();
			cb.Blit(null, BuiltinRenderTextureType.CurrentActive, material);
			cam.AddCommandBuffer(CameraEvent.AfterEverything, cb);
			cam.depthTextureMode = depthTextureMode;
		}
		
		#endregion
		
		#region Internal structs
		
		enum ReplacementModes {
			ObjectId 			= 0,
			CatergoryId			= 1,
			DepthCompressed		= 2,
			DepthMultichannel	= 3,
			Normals				= 4
		};

		#endregion
		
		#region Utility data

		const int c_depthSize = 24;
		
		// pass configuration (factory, kind of)
		static readonly Dictionary<PassKind, CapturePass> s_allCapturePasses = new Dictionary<PassKind, CapturePass>
		{
			{ PassKind.EImage, new CapturePass(PassKind.EImage, "_img", true) },
			{ PassKind.EId, new CapturePass(PassKind.EId, "_id", false) },
			{ PassKind.ELayer, new CapturePass(PassKind.ELayer, "_layer", false) },
			{ PassKind.EDepth, new CapturePass(PassKind.EDepth, "_depth", true) },
			{ PassKind.ENormals, new CapturePass(PassKind.ENormals, "_normals", false) },
			// (see issue with Motion Vectors in @KNOWN ISSUES), they need rescaling (doing that anyway.)
			{ PassKind.EFlow, new CapturePass(PassKind.EFlow, "_flow", false) } 
		};
		#endregion

		#region Private data
		
        Vector2Int m_dim;
		// cached materials
		Material m_opticalFlowMaterial;

		Camera m_cam;
		CapturePass[] m_capturePasses;

		RenderTexture m_fullRenderTexture;

        byte[] m_outBuffer;

        Util.SnapshotSaver m_saver;
        

        // Note: when m_currentFrameIndex starts at 0 the saving routines kicks in
        // before any rendering has actually been done,
        // resulting in empty textures. Starting at 1 prevents this.
        // Now, for the Motion Vectors we need one previously rendered image,
        // Because they are computed as the pixel-wise difference between two frames.
        // In conclusion, we should start at 2.
        // However, that does not work, since that results in the Motion Vectors not being
        // computed, for some reason? So we'll discard the first frame in a couple, and take only the second.
        int m_currentFrameIndex = 1;
        int m_rectifiedFrameIndex;
        
        bool m_shouldUseQuickFix;
        
        int m_numberCaptured;

        #endregion

		#region Unity Editor specifics 
		#if UNITY_EDITOR
		GameObject m_lastSelectedGO;
		int m_lastSelectedGOLayer = -1;
		string m_lastSelectedGOTag = "unknown";
		bool DetectPotentialSceneChangeInEditor()
		{
			bool change = false;
			// there is no callback in Unity Editor to automatically detect changes in scene objects
			// as a workaround lets track selected objects and check, if properties that are 
			// interesting for us (layer or tag) did not change since the last frame
			if (UnityEditor.Selection.transforms.Length > 1)
			{
				// multiple objects are selected, all bets are off!
				// we have to assume these objects are being edited
				change = true;
				m_lastSelectedGO = null;
			}
			else if (UnityEditor.Selection.activeGameObject)
			{
				var go = UnityEditor.Selection.activeGameObject;
				// check if layer or tag of a selected object have changed since the last frame
				var potentialChangeHappened = m_lastSelectedGOLayer != go.layer || m_lastSelectedGOTag != go.tag;
				if (go == m_lastSelectedGO && potentialChangeHappened)
					change = true;

				m_lastSelectedGO = go;
				m_lastSelectedGOLayer = go.layer;
				m_lastSelectedGOTag = go.tag;
			}

			return change;
		}
		#endif // UNITY_EDITOR

		#endregion
	}
		
}
