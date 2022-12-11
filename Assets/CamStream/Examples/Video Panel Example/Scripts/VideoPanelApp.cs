//  
// Copyright (c) 2017 Vulcan, Inc. All rights reserved.  
// Licensed under the Apache 2.0 license. See LICENSE file in the project root for full license information.
//

using UnityEngine;
using System;
using HoloLensCameraStream;
using System.Collections.Generic;
using Klak.TestTools;
using YoloV4Tiny;


#if WINDOWS_UWP && XR_PLUGIN_OPENXR
using Windows.Perception.Spatial;
#endif

/// <summary>
/// This example gets the video frames at 30 fps and displays them on a Unity texture,
/// and displayed the debug information in front.
/// 
/// **Add Define Symbols:**
/// Open **File > Build Settings > Player Settings > Other Settings** and add the following to `Scripting Define Symbols` depending on the XR system used in your project;
/// - Legacy built-in XR: `BUILTIN_XR`';
/// - XR Plugin Management (Windows Mixed Reality): `XR_PLUGIN_WINDOWSMR`;
/// - XR Plugin Management (OpenXR):`XR_PLUGIN_OPENXR`.
/// </summary>
public class VideoPanelApp : MonoBehaviour
{
    byte[] _latestImageBytes;
    HoloLensCameraStream.Resolution _resolution;

    //"Injected" objects.
    VideoPanel _videoPanelUI;
    VideoCapture _videoCapture;
    public TextMesh _displayText;

    IntPtr _spatialCoordinateSystemPtr;

#if WINDOWS_UWP && XR_PLUGIN_OPENXR
    SpatialCoordinateSystem _spatialCoordinateSystem;
#endif

    Queue<Action> _mainThreadActions;

    // Stuff from yolov4
    ObjectDetector _detector;

    // [SerializeField] ImageSource _source = null;
    // [SerializeField, Range(0, 1)] float _threshold = 0.5f;
    #region Editable attributes
    [SerializeField] ImageSource _source = null;
    [SerializeField] ResourceSet _resources = null;
    #endregion
    
    public static string[] _labels = new[]
    {
        "Plane", "Bicycle", "Bird", "Boat",
        "Bottle", "Bus", "Car", "Cat",
        "Chair", "Cow", "Table", "Dog",
        "Horse", "Motorbike", "Person", "Plant",
        "Sheep", "Sofa", "Train", "TV"
    };

    Texture2D vidtex;
    // [SerializeField] Marker _markerPrefab = null;



    void Start()
    {
        _mainThreadActions = new Queue<Action>();

        //Fetch a pointer to Unity's spatial coordinate system if you need pixel mapping
#if WINDOWS_UWP

#if XR_PLUGIN_WINDOWSMR

        _spatialCoordinateSystemPtr = UnityEngine.XR.WindowsMR.WindowsMREnvironment.OriginSpatialCoordinateSystem;

#elif XR_PLUGIN_OPENXR

        _spatialCoordinateSystem = Microsoft.MixedReality.OpenXR.PerceptionInterop.GetSceneCoordinateSystem(UnityEngine.Pose.identity) as SpatialCoordinateSystem;

#elif BUILTIN_XR

#if UNITY_2017_2_OR_NEWER
        _spatialCoordinateSystemPtr = UnityEngine.XR.WSA.WorldManager.GetNativeISpatialCoordinateSystemPtr();
#else
        _spatialCoordinateSystemPtr = UnityEngine.VR.WSA.WorldManager.GetNativeISpatialCoordinateSystemPtr();
#endif

#endif

#endif

        //Call this in Start() to ensure that the CameraStreamHelper is already "Awake".
        CameraStreamHelper.Instance.GetVideoCaptureAsync(OnVideoCaptureCreated);
        //You could also do this "shortcut":
        //CameraStreamManager.Instance.GetVideoCaptureAsync(v => videoCapture = v);

        _videoPanelUI = GameObject.FindObjectOfType<VideoPanel>();
        _videoPanelUI.meshRenderer.transform.localScale = new Vector3(1, -1, 1);

        // Create detector objects
        _detector = new ObjectDetector(_resources);
        // vidtex = new Texture2D(2,2, TextureFormat.BGRA32, false);
        // for (var i = 0; i < _markers.Length; i++)
        //     _markers[i] = Instantiate(_markerPrefab, _preview.transform);
    }

    void OnDisable()
      => _detector.Dispose();


    private void Update()
    {
        lock (_mainThreadActions)
        {
            while (_mainThreadActions.Count > 0)
            {
                _mainThreadActions.Dequeue().Invoke();
            }
        }
    }

    private void Enqueue(Action action)
    {
        lock (_mainThreadActions)
        {
            _mainThreadActions.Enqueue(action);
        }
    }

    private void OnDestroy()
    {
        if (_videoCapture != null)
        {
            _videoCapture.FrameSampleAcquired -= OnFrameSampleAcquired;
            _videoCapture.Dispose();
        }
    }

    void OnVideoCaptureCreated(VideoCapture videoCapture)
    {
        if (videoCapture == null)
        {
            Debug.LogError("Did not find a video capture object. You may not be using the HoloLens.");
            Enqueue(() => SetText("Did not find a video capture object. You may not be using the HoloLens."));
            return;
        }

        this._videoCapture = videoCapture;

        //Request the spatial coordinate ptr if you want fetch the camera and set it if you need to 
#if WINDOWS_UWP

#if XR_PLUGIN_OPENXR
        CameraStreamHelper.Instance.SetNativeISpatialCoordinateSystem(_spatialCoordinateSystem);
#elif XR_PLUGIN_WINDOWSMR || BUILTIN_XR
        CameraStreamHelper.Instance.SetNativeISpatialCoordinateSystemPtr(_spatialCoordinateSystemPtr);
#endif

#endif

        _resolution = CameraStreamHelper.Instance.GetLowestResolution();
        float frameRate = CameraStreamHelper.Instance.GetHighestFrameRate(_resolution);
        videoCapture.FrameSampleAcquired += OnFrameSampleAcquired;

        //You don't need to set all of these params.
        //I'm just adding them to show you that they exist.
        CameraParameters cameraParams = new CameraParameters();
        cameraParams.cameraResolutionHeight = _resolution.height;
        cameraParams.cameraResolutionWidth = _resolution.width;
        cameraParams.frameRate = Mathf.RoundToInt(frameRate);
        cameraParams.pixelFormat = CapturePixelFormat.BGRA32;
        cameraParams.rotateImage180Degrees = false;
        cameraParams.enableHolograms = false;

        Debug.Log("Configuring camera: " + _resolution.width + "x" + _resolution.height + "x" + cameraParams.frameRate + " | " + cameraParams.pixelFormat);
        Enqueue(() => SetText("Configuring camera: " + _resolution.width + "x" + _resolution.height + "x" + cameraParams.frameRate + " | " + cameraParams.pixelFormat));

        Enqueue(() => _videoPanelUI.SetResolution(_resolution.width, _resolution.height));
        vidtex = new Texture2D(_resolution.width ,_resolution.height, TextureFormat.BGRA32, false);
        videoCapture.StartVideoModeAsync(cameraParams, OnVideoModeStarted);
    }

    void OnVideoModeStarted(VideoCaptureResult result)
    {
        if (result.success == false)
        {
            Debug.LogWarning("Could not start video mode.");
            Enqueue(() => SetText("Could not start video mode."));
            return;
        }

        Debug.Log("Video capture started.");
        Enqueue(() => SetText("Video capture started."));
    }

    void OnFrameSampleAcquired(VideoCaptureSample sample)
    {
        lock (_mainThreadActions)
        {
            if (_mainThreadActions.Count > 2)
            {
                sample.Dispose();
                return;
            }
        }

        //When copying the bytes out of the buffer, you must supply a byte[] that is appropriately sized.
        //You can reuse this byte[] until you need to resize it (for whatever reason).
        if (_latestImageBytes == null || _latestImageBytes.Length < sample.dataLength)
        {
            _latestImageBytes = new byte[sample.dataLength];
        }
        sample.CopyRawImageDataIntoBuffer(_latestImageBytes);


        //If you need to get the cameraToWorld matrix for purposes of compositing you can do it like this
        float[] cameraToWorldMatrixAsFloat;
        if (sample.TryGetCameraToWorldMatrix(out cameraToWorldMatrixAsFloat) == false)
        {
            //return;
        }

        //If you need to get the projection matrix for purposes of compositing you can do it like this
        float[] projectionMatrixAsFloat;
        if (sample.TryGetProjectionMatrix(out projectionMatrixAsFloat) == false)
        {
            //return;
        }

        // Right now we pass things across the pipe as a float array then convert them back into UnityEngine.Matrix using a utility method
        Matrix4x4 cameraToWorldMatrix = LocatableCameraUtils.ConvertFloatArrayToMatrix4x4(cameraToWorldMatrixAsFloat);
        Matrix4x4 projectionMatrix = LocatableCameraUtils.ConvertFloatArrayToMatrix4x4(projectionMatrixAsFloat);

        Enqueue(() =>
        {
            vidtex.LoadRawTextureData(_latestImageBytes);
            vidtex.Apply();
            // _videoPanelUI.SetBytes(_latestImageBytes);
            _videoPanelUI.setTexture(vidtex);

#if XR_PLUGIN_WINDOWSMR || XR_PLUGIN_OPENXR
            // It appears that the Legacy built-in XR environment automatically applies the Holelens Head Pose to Unity camera transforms,
            // but not to the new XR system (XR plugin management) environment.
            // Here the cameraToWorldMatrix is applied to the camera transform as an alternative to Head Pose,
            // so the position of the displayed video panel is significantly misaligned. If you want to apply a more accurate Head Pose, use MRTK.

            Camera unityCamera = Camera.main;
            Matrix4x4 invertZScaleMatrix = Matrix4x4.Scale(new Vector3(1, 1, -1));
            Matrix4x4 localToWorldMatrix = cameraToWorldMatrix * invertZScaleMatrix;
            unityCamera.transform.localPosition = localToWorldMatrix.GetColumn(3);
            unityCamera.transform.localRotation = Quaternion.LookRotation(localToWorldMatrix.GetColumn(2), localToWorldMatrix.GetColumn(1));
#endif

            // Loading bytes into texture so we can feed it to th emodel
            // _detector.ProcessImage(vidtex, .2f);
            // if (vidtex == null){
            //     vidtex = new Texture2D(2,2);
            //     Debug.Log("vidtex is null, making new one");
            // }

            // if (vidtex == null){
            //     Enqueue(() => SetText("vidtex is null even after init"));
            // } else{
            //     vidtex.LoadRawTextureData(_latestImageBytes);
            //     // BGRA32 | 407040
            //     // vidtex.Apply();
            //     Debug.Log("vidtex is not null, loaded bytes");
                
                _detector.ProcessImage(vidtex, .5f);
                // Debug.Log("detector processed image, got " + _detector.Detections + " detections");

                String allpreds = "";
                foreach (var d in _detector.Detections)
                {  
                    
                    allpreds += _labels[d.classIndex] + " " + d.score + "\n";
                }
                Debug.Log("allpreds: " + allpreds);
                Enqueue(() => SetText(allpreds));
            // }

            // Debug.Log("Got frame: " + sample.FrameWidth + "x" + sample.FrameHeight + " | " + sample.pixelFormat + " | " + sample.dataLength);
            // // Enqueue(() => SetText("Got frame: " + sample.FrameWidth + "x" + sample.FrameHeight + " | " + sample.pixelFormat + " | " + sample.dataLength));

        });

        sample.Dispose();
    }

    private void SetText(string text)
    {
        if (_displayText != null)
        {
            // _displayText.text += text + "\n";
            _displayText.text = text;
        }
    }
}