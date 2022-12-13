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
using TMPro;

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
    //[SerializeField] GameObject _worldparent = null;
    #endregion
    
    public static string[] _labels = new[]
    {
        "Plane", "Bicycle", "Bird", "Boat",
        "Bottle", "Bus", "Car", "Cat",
        "Chair", "Cow", "Table", "Dog",
        "Horse", "Motorbike", "Person", "Plant",
        "Sheep", "Sofa", "Train", "TV"
    };

    // HologramCollection holoco;
    public GameObject buttonPrefab;
    public static int numBalls = 4;
    public GameObject[] _balls;//=new HologramCollection[numBalls];
    public Color[] _colors=new Color[]{
        new Color(1,0,0,1),
        new Color(0,1,0,1),
        new Color(0,0,1,1),
        new Color(1,1,0,1)
    };

    // private GameObject HOLOLABEL;
    Texture2D vidtex;



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
        _balls = new GameObject[numBalls];
        for (var i = 0; i < _balls.Length; i++){
            // _balls[i] = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            _balls[i] = Instantiate(buttonPrefab , new Vector3(0, 3, 0), Quaternion.identity);
            // var ballrender = _balls[i].GetComponent<Renderer>();
            // ballrender.material.color = _colors[i];
            // ballrender.material.SetColor("_Color", _colors[i]);
            _balls[i].transform.localScale = new Vector3(0.2f,0.2f,0.2f);
        }

        
        _colors=new Color[]{
            new Color(1,0,0,1),
            new Color(0,1,0,1),
            new Color(0,0,1,1),
            new Color(1,1,0,1)
        };
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






    void buttonSetText(GameObject button, string text){
        TextMeshPro text_english = button.transform.GetChild(0).GetChild(1).gameObject.GetComponent<TextMeshPro>();
        TextMeshPro text_german = button.transform.GetChild(0).GetChild(2).gameObject.GetComponent<TextMeshPro>();
        text_english.SetText(text);
    }

    bool setPosition = false;
    int count = 0;
    int countTrig = 90;

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
            _videoPanelUI.setTexture(vidtex);
            Camera unityCamera = Camera.main;
            Matrix4x4 invertZScaleMatrix = Matrix4x4.Scale(new Vector3(1, 1, -1));
            Matrix4x4 localToWorldMatrix = cameraToWorldMatrix * invertZScaleMatrix;
            unityCamera.transform.localPosition = localToWorldMatrix.GetColumn(3);
            unityCamera.transform.localRotation = Quaternion.LookRotation(localToWorldMatrix.GetColumn(2), localToWorldMatrix.GetColumn(1));




            // Run detector
            count++;
            if (count == countTrig){
                setPosition = false;
                count = 0;
            }
            if (!setPosition){
                 _detector.ProcessImage(vidtex, .2f);
                Vector3 org = cameraToWorldMatrix.GetColumn(3);

                // For each prediction, get the xy on the image where it's detected 
                // Also accumulate a big string desrcibin all the detections
                var i = 0;
                var alldetects = ""; 
                Vector3 inverseNormal = -cameraToWorldMatrix.GetColumn(2);
                foreach (var d in _detector.Detections){
                    // var d = _detector.Detections[i];
                    // float xloc = (d.x+d.w/2)*_resolution.width;
                    // float yloc = (d.y+d.h/2)*_resolution.height;
                    float xloc = (d.x)*_resolution.width;
                    float yloc = (d.y + d.h/2)*_resolution.height;
                    
                    string cText = _labels[d.classIndex] + " " + d.score + " " + xloc + " " + yloc + "\n";
                    alldetects += cText;
                    buttonSetText(_balls[i], _labels[d.classIndex] + " " + d.score);

                    Vector3 detectDirectionVec = LocatableCameraUtils.PixelCoordToWorldCoord(cameraToWorldMatrix, projectionMatrix, _resolution, new Vector2(xloc , yloc));
                    _balls[i].transform.position = org + detectDirectionVec;
                    _balls[i].transform.rotation = Quaternion.LookRotation(inverseNormal, cameraToWorldMatrix.GetColumn(1));
                    // moveBallInDirection(org, detectDirectionVec, i);
                    // break;
                    i++;
                    if (i == numBalls) break;
                }

                
                // foreach (var d in _detector.Detections){
                //     // var d = _detector.Detections[i];
                //     float xloc = (d.x+d.w/2)*_resolution.width;
                //     float yloc = (d.y+d.h/2)*_resolution.height;
                    
                //     string cText = _labels[d.classIndex] + " " + d.score + " " + xloc + " " + yloc + "\n";
                //     alldetects += cText;
                //     buttonSetText(_balls[i], _labels[d.classIndex] + " " + d.score);

                //     Vector3 detectDirectionVec = LocatableCameraUtils.PixelCoordToWorldCoord(cameraToWorldMatrix, projectionMatrix, _resolution, new Vector2(xloc , yloc));
                //     _balls[i].transform.position = org + detectDirectionVec;
                //     // _balls[i].transform.rotation = Quaternion.LookRotation(inverseNormal, cameraToWorldMatrix.GetColumn(1));
                //     // moveBallInDirection(org, detectDirectionVec, i);
                //     // break;
                //     i++;
                //     if (i == numBalls) break;
                // }



                // Vector3 inverseNormal = -cameraToWorldMatrix.GetColumn(2);
                // Vector3 pp2 = LocatableCameraUtils.PixelCoordToWorldCoord(cameraToWorldMatrix, projectionMatrix, _resolution, new Vector2(_resolution.width/2, _resolution.height/2));
                // // org = cameraToWorldMatrix.GetColumn(3);
                // // var inmyface = _balls[0]
                // var inmyface = _balls[0];
                // // var inmyface = holocollection;
                // inmyface.transform.position = org + pp2;
                // inmyface.transform.rotation = Quaternion.LookRotation(inverseNormal, cameraToWorldMatrix.GetColumn(1));


                // TextMesh textObject = inmyface.GetComponent<TextMesh>();
                // textObject.text = "i see u " + count.ToString();

                // Vector3 pp2 = LocatableCameraUtils.PixelCoordToWorldCoord(cameraToWorldMatrix, projectionMatrix, _resolution, new Vector2(_resolution.width/2, _resolution.height/2));
                // Vector3 org = cameraToWorldMatrix.GetColumn(3);
                // _balls[0].transform.position = org + pp2;







                // Debug.Log("pp: " + pp2);
                // Enqueue(() => SetText( "cam2world 'from'" + cameraToWorldMatrix.GetColumn(3).ToString() +
                //     "PP: " + pp.ToString() + "pp2: " + pp2.ToString()));


                // Drawing static sphere in user face
                // _balls[2].transform.position = Camera.main.transform.position + Camera.main.transform.forward * -1;
                // Gizmos.color = Color.blue;
                // // Gizmos.DrawSphere(Camera.main.transform.position, 1);
                // Gizmos.DrawSphere(Camera.main.transform.position + Camera.main.transform.forward * 2, 1);


                // Draw static sphere in world
                // _balls[3].transform.position = new Vector3(0, 0, 1); // in middle of the video panel
                // balls[0] in my head

                Enqueue(() => SetText(alldetects));
                setPosition = true;
            }
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

// //  
// // Copyright (c) 2017 Vulcan, Inc. All rights reserved.  
// // Licensed under the Apache 2.0 license. See LICENSE file in the project root for full license information.
// //

// using UnityEngine;
// using System;
// using HoloLensCameraStream;
// using System.Collections.Generic;
// using Klak.TestTools;
// using YoloV4Tiny;
// using TMPro;

// #if WINDOWS_UWP && XR_PLUGIN_OPENXR
// using Windows.Perception.Spatial;
// #endif

// /// <summary>
// /// This example gets the video frames at 30 fps and displays them on a Unity texture,
// /// and displayed the debug information in front.
// /// 
// /// **Add Define Symbols:**
// /// Open **File > Build Settings > Player Settings > Other Settings** and add the following to `Scripting Define Symbols` depending on the XR system used in your project;
// /// - Legacy built-in XR: `BUILTIN_XR`';
// /// - XR Plugin Management (Windows Mixed Reality): `XR_PLUGIN_WINDOWSMR`;
// /// - XR Plugin Management (OpenXR):`XR_PLUGIN_OPENXR`.
// /// </summary>
// public class VideoPanelApp : MonoBehaviour
// {
//     byte[] _latestImageBytes;
//     HoloLensCameraStream.Resolution _resolution;

//     //"Injected" objects.
//     VideoPanel _videoPanelUI;
//     VideoCapture _videoCapture;
//     public TextMesh _displayText;

//     IntPtr _spatialCoordinateSystemPtr;

// #if WINDOWS_UWP && XR_PLUGIN_OPENXR
//     SpatialCoordinateSystem _spatialCoordinateSystem;
// #endif

//     Queue<Action> _mainThreadActions;

//     // Stuff from yolov4
//     ObjectDetector _detector;

//     // [SerializeField] ImageSource _source = null;
//     // [SerializeField, Range(0, 1)] float _threshold = 0.5f;
//     #region Editable attributes
//     [SerializeField] ImageSource _source = null;
//     [SerializeField] ResourceSet _resources = null;
//     //[SerializeField] GameObject _worldparent = null;
//     #endregion
    
//     public static string[] _labels = new[]
//     {
//         "Plane", "Bicycle", "Bird", "Boat",
//         "Bottle", "Bus", "Car", "Cat",
//         "Chair", "Cow", "Table", "Dog",
//         "Horse", "Motorbike", "Person", "Plant",
//         "Sheep", "Sofa", "Train", "TV"
//     };

//     // HologramCollection holoco;
//     public GameObject buttonPrefab;
//     public static int numBalls = 4;
//     private GameObject[] _balls;//=new HologramCollection[numBalls];
//     private Color[] _colors=new Color[]{
//         new Color(1,0,0,1),
//         new Color(0,1,0,1),
//         new Color(0,0,1,1),
//         new Color(1,1,0,1)
//     };

//     // private GameObject HOLOLABEL;
//     Texture2D vidtex;



//     void Start()
//     {
//         _mainThreadActions = new Queue<Action>();

//         //Fetch a pointer to Unity's spatial coordinate system if you need pixel mapping
// #if WINDOWS_UWP

//     #if XR_PLUGIN_WINDOWSMR

//             _spatialCoordinateSystemPtr = UnityEngine.XR.WindowsMR.WindowsMREnvironment.OriginSpatialCoordinateSystem;

//     #elif XR_PLUGIN_OPENXR

//             _spatialCoordinateSystem = Microsoft.MixedReality.OpenXR.PerceptionInterop.GetSceneCoordinateSystem(UnityEngine.Pose.identity) as SpatialCoordinateSystem;

//     #elif BUILTIN_XR

//         #if UNITY_2017_2_OR_NEWER
//                 _spatialCoordinateSystemPtr = UnityEngine.XR.WSA.WorldManager.GetNativeISpatialCoordinateSystemPtr();
//         #else
//                 _spatialCoordinateSystemPtr = UnityEngine.VR.WSA.WorldManager.GetNativeISpatialCoordinateSystemPtr();
//         #endif

//     #endif

// #endif

//         // Create detector objects
//         _detector = new ObjectDetector(_resources);
//         _balls = new GameObject[numBalls];
//         for (var i = 0; i < _balls.Length; i++){
//             // _balls[i] = GameObject.CreatePrimitive(PrimitiveType.Sphere);
//             _balls[i] = Instantiate(buttonPrefab , new Vector3(i*.3f, i*.3f, 1), Quaternion.identity);
//             // var ballrender = _balls[i].GetComponent<Renderer>();
//             // ballrender.material.color = _colors[i];
//             // ballrender.material.SetColor("_Color", _colors[i]);
//             _balls[i].transform.localScale = new Vector3(0.02f,0.02f,0.02f);
//         }
//         Debug.Log("Created balls" + _balls.Length);
//         // Debug.Log("ball 0 color: " + _balls[0].GetComponent<Renderer>().material.color);
//         // Debug.Log("ball 0 id" + _balls[0].GetInstanceID());
//         // Debug.Log("ball 1 id" + _balls[1].GetInstanceID());
        
//         _colors=new Color[]{
//             new Color(1,0,0,1),
//             new Color(0,1,0,1),
//             new Color(0,0,1,1),
//             new Color(1,1,0,1)
//         };


//         //Call this in Start() to ensure that the CameraStreamHelper is already "Awake".
//         CameraStreamHelper.Instance.GetVideoCaptureAsync(OnVideoCaptureCreated);
//         //You could also do this "shortcut":
//         //CameraStreamManager.Instance.GetVideoCaptureAsync(v => videoCapture = v);

//         _videoPanelUI = GameObject.FindObjectOfType<VideoPanel>();
//         _videoPanelUI.meshRenderer.transform.localScale = new Vector3(1, -1, 1);


//         // vidtex = new Texture2D(2,2, TextureFormat.BGRA32, false);
//         // for (var i = 0; i < _markers.Length; i++)
//         //     _markers[i] = Instantiate(_markerPrefab, _preview.transform);
//     }

//     void OnDisable()
//       => _detector.Dispose();


//     private void Update()
//     {
//         lock (_mainThreadActions)
//         {
//             while (_mainThreadActions.Count > 0)
//             {
//                 _mainThreadActions.Dequeue().Invoke();
//             }
//         }
//     }

//     private void Enqueue(Action action)
//     {
//         lock (_mainThreadActions)
//         {
//             _mainThreadActions.Enqueue(action);
//         }
//     }

//     private void OnDestroy()
//     {
//         if (_videoCapture != null)
//         {
//             _videoCapture.FrameSampleAcquired -= OnFrameSampleAcquired;
//             _videoCapture.Dispose();
//         }
//     }

//     void OnVideoCaptureCreated(VideoCapture videoCapture)
//     {
//         if (videoCapture == null)
//         {
//             Debug.LogError("Did not find a video capture object. You may not be using the HoloLens.");
//             Enqueue(() => SetText("Did not find a video capture object. You may not be using the HoloLens."));
//             return;
//         }

//         this._videoCapture = videoCapture;

//         //Request the spatial coordinate ptr if you want fetch the camera and set it if you need to 
// #if WINDOWS_UWP

// #if XR_PLUGIN_OPENXR
//         CameraStreamHelper.Instance.SetNativeISpatialCoordinateSystem(_spatialCoordinateSystem);
// #elif XR_PLUGIN_WINDOWSMR || BUILTIN_XR
//         CameraStreamHelper.Instance.SetNativeISpatialCoordinateSystemPtr(_spatialCoordinateSystemPtr);
// #endif

// #endif

//         _resolution = CameraStreamHelper.Instance.GetLowestResolution();
//         float frameRate = CameraStreamHelper.Instance.GetHighestFrameRate(_resolution);
//         videoCapture.FrameSampleAcquired += OnFrameSampleAcquired;

//         //You don't need to set all of these params.
//         //I'm just adding them to show you that they exist.
//         CameraParameters cameraParams = new CameraParameters();
//         cameraParams.cameraResolutionHeight = _resolution.height;
//         cameraParams.cameraResolutionWidth = _resolution.width;
//         cameraParams.frameRate = Mathf.RoundToInt(frameRate);
//         cameraParams.pixelFormat = CapturePixelFormat.BGRA32;
//         cameraParams.rotateImage180Degrees = false;
//         cameraParams.enableHolograms = false;

//         Debug.Log("Configuring camera: " + _resolution.width + "x" + _resolution.height + "x" + cameraParams.frameRate + " | " + cameraParams.pixelFormat);
//         Enqueue(() => SetText("Configuring camera: " + _resolution.width + "x" + _resolution.height + "x" + cameraParams.frameRate + " | " + cameraParams.pixelFormat));

//         Enqueue(() => _videoPanelUI.SetResolution(_resolution.width, _resolution.height));
//         vidtex = new Texture2D(_resolution.width ,_resolution.height, TextureFormat.BGRA32, false);
//         videoCapture.StartVideoModeAsync(cameraParams, OnVideoModeStarted);
//     }

//     void OnVideoModeStarted(VideoCaptureResult result)
//     {
//         if (result.success == false)
//         {
//             Debug.LogWarning("Could not start video mode.");
//             Enqueue(() => SetText("Could not start video mode."));
//             return;
//         }

//         Debug.Log("Video capture started.");
//         Enqueue(() => SetText("Video capture started."));
//     }






//     void buttonSetText(GameObject button, string text){
//         TextMeshPro text_english = button.transform.GetChild(0).GetChild(1).gameObject.GetComponent<TextMeshPro>();
//         TextMeshPro text_german = button.transform.GetChild(0).GetChild(2).gameObject.GetComponent<TextMeshPro>();
//         text_english.SetText(text);
//     }

//     bool setPosition = false;
//     int count = 0;
//     int countTrig = 90;

//     void OnFrameSampleAcquired(VideoCaptureSample sample)
//     {
//         lock (_mainThreadActions)
//         {
//             if (_mainThreadActions.Count > 2)
//             {
//                 sample.Dispose();
//                 return;
//             }
//         }

//         //When copying the bytes out of the buffer, you must supply a byte[] that is appropriately sized.
//         //You can reuse this byte[] until you need to resize it (for whatever reason).
//         if (_latestImageBytes == null || _latestImageBytes.Length < sample.dataLength)
//         {
//             _latestImageBytes = new byte[sample.dataLength];
//         }
//         sample.CopyRawImageDataIntoBuffer(_latestImageBytes);


//         //If you need to get the cameraToWorld matrix for purposes of compositing you can do it like this
//         float[] cameraToWorldMatrixAsFloat;
//         if (sample.TryGetCameraToWorldMatrix(out cameraToWorldMatrixAsFloat) == false)
//         {
//             //return;
//         }

//         //If you need to get the projection matrix for purposes of compositing you can do it like this
//         float[] projectionMatrixAsFloat;
//         if (sample.TryGetProjectionMatrix(out projectionMatrixAsFloat) == false)
//         {
//             //return;
//         }

//         // Right now we pass things across the pipe as a float array then convert them back into UnityEngine.Matrix using a utility method
//         Matrix4x4 cameraToWorldMatrix = LocatableCameraUtils.ConvertFloatArrayToMatrix4x4(cameraToWorldMatrixAsFloat);
//         Matrix4x4 projectionMatrix = LocatableCameraUtils.ConvertFloatArrayToMatrix4x4(projectionMatrixAsFloat);

//         Enqueue(() =>
//         {
//             vidtex.LoadRawTextureData(_latestImageBytes);
//             vidtex.Apply();
//             _videoPanelUI.setTexture(vidtex);
//             Camera unityCamera = Camera.main;
//             Matrix4x4 invertZScaleMatrix = Matrix4x4.Scale(new Vector3(1, 1, -1));
//             Matrix4x4 localToWorldMatrix = cameraToWorldMatrix * invertZScaleMatrix;
//             unityCamera.transform.localPosition = localToWorldMatrix.GetColumn(3);
//             unityCamera.transform.localRotation = Quaternion.LookRotation(localToWorldMatrix.GetColumn(2), localToWorldMatrix.GetColumn(1));




//             // Run detector
//             count++;
//             if (count % countTrig == 0){
//                 setPosition = false;
//                 // count = 0;
//             }
//             if (!setPosition){
//                  _detector.ProcessImage(vidtex, .2f);
//                 Vector3 org = cameraToWorldMatrix.GetColumn(3);

//                 // For each prediction, get the xy on the image where it's detected 
//                 // Also accumulate a big string desrcibin all the detections
//                 int i = 0;
//                 string alldetects = ""; 
//                 Vector3 inverseNormal = -cameraToWorldMatrix.GetColumn(2);
//                 foreach (var d in _detector.Detections){
//                     // var d = _detector.Detections[i];
//                     float xloc = (d.x+d.w/2)*_resolution.width;
//                     float yloc = (d.y+d.h/2)*_resolution.height;
                    
//                     string cText = _labels[d.classIndex] + " " + d.score + " " + xloc + " " + yloc + "\n";
//                     alldetects += cText;
//                     buttonSetText(_balls[i], _labels[d.classIndex] + " " + d.score);

//                     Vector3 detectDirectionVec = LocatableCameraUtils.PixelCoordToWorldCoord(cameraToWorldMatrix, projectionMatrix, _resolution, new Vector2(xloc , yloc));
//                     _balls[i].transform.position = org + detectDirectionVec;
//                     // _balls[i].transform.rotation = Quaternion.LookRotation(inverseNormal, cameraToWorldMatrix.GetColumn(1));
//                     // moveBallInDirection(org, detectDirectionVec, i);
//                     // break;
//                     i++;
//                     if (i == numBalls) break;
//                 }



//                 // 
//                 // Vector3 pp2 = LocatableCameraUtils.PixelCoordToWorldCoord(cameraToWorldMatrix, projectionMatrix, _resolution, new Vector2(_resolution.width/2, _resolution.height/2));
//                 // // org = cameraToWorldMatrix.GetColumn(3);
//                 // // var inmyface = _balls[0]
//                 // var inmyface = _balls[0];
//                 // // var inmyface = holocollection;
//                 // inmyface.transform.position = org + pp2;
//                 // inmyface.transform.rotation = Quaternion.LookRotation(inverseNormal, cameraToWorldMatrix.GetColumn(1));


//                 // TextMesh textObject = inmyface.GetComponent<TextMesh>();
//                 // textObject.text = "i see u " + count.ToString();

//                 // Vector3 pp2 = LocatableCameraUtils.PixelCoordToWorldCoord(cameraToWorldMatrix, projectionMatrix, _resolution, new Vector2(_resolution.width/2, _resolution.height/2));
//                 // Vector3 org = cameraToWorldMatrix.GetColumn(3);
//                 // _balls[0].transform.position = org + pp2;







//                 // Debug.Log("pp: " + pp2);
//                 // Enqueue(() => SetText( "cam2world 'from'" + cameraToWorldMatrix.GetColumn(3).ToString() +
//                 //     "PP: " + pp.ToString() + "pp2: " + pp2.ToString()));


//                 // Drawing static sphere in user face
//                 // _balls[2].transform.position = Camera.main.transform.position + Camera.main.transform.forward * -1;
//                 // Gizmos.color = Color.blue;
//                 // // Gizmos.DrawSphere(Camera.main.transform.position, 1);
//                 // Gizmos.DrawSphere(Camera.main.transform.position + Camera.main.transform.forward * 2, 1);


//                 // Draw static sphere in world
//                 // _balls[3].transform.position = new Vector3(0, 0, 1); // in middle of the video panel
//                 // balls[0] in my head

//                 Enqueue(() => SetText(alldetects + "\nball 0 position:" + _balls[0].transform.position.ToString()));
//                 setPosition = true;
//             }
//             // Debug.Log("Got frame: " + sample.FrameWidth + "x" + sample.FrameHeight + " | " + sample.pixelFormat + " | " + sample.dataLength);
//             // // Enqueue(() => SetText("Got frame: " + sample.FrameWidth + "x" + sample.FrameHeight + " | " + sample.pixelFormat + " | " + sample.dataLength));

//         });

//         sample.Dispose();
//     }

//     private void SetText(string text)
//     {
//         if (_displayText != null)
//         {
//             // _displayText.text += text + "\n";
//             _displayText.text = text;
//         }
//     }
// }

