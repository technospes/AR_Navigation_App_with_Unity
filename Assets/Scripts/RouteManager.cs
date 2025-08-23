using Mapbox.Directions;
using Mapbox.Unity;
using Mapbox.Unity.Location;
using Mapbox.Unity.Map;
using Mapbox.Unity.Utilities;
using Mapbox.Utils;
using System.Collections;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.XR.ARFoundation;
using UnityEngine.XR.ARSubsystems;
using System.Linq;
using Mapbox.MapMatching;
using System.Globalization;
using UnityEngine.UIElements;

#if UNITY_ANDROID
using UnityEngine.Android;
#endif

public class RouteManager : MonoBehaviour
{
    [Header("Required Components")]
    [SerializeField] private AbstractMap map;
    public GameObject arrowPrefab;
    public Transform arRouteParent;
    public OnscreenArrowLogger arrowLogger;

    [Header("AR Components")]
    public ARPlaneManager planeManager;
    public ARAnchorManager anchorManager;
    public ARRaycastManager raycastManager;
    public Camera arCamera;

    [Header("Location Services")]
    public bool useDeviceGPS = true;
    private Quaternion northAlignment = Quaternion.identity;
    private Vector2d routeOrigin;

    [Header("Arrow Orientation")]
    public float arrowYawOffsetDegrees = 0f;

    [Header("Navigation Settings")]
    public float arrowSpacing = 5f;
    public int maxArrows = 15;
    public float arrowScale = 0.5f;
    public float arrowHeightOffset = 0.1f;
    public float maxVisibleDistance = 100f;

    [Header("Coordinate Conversion")]
    public float mapToARScale = 1f;
    public Vector3 arOriginOffset = Vector3.zero;
    public bool autoCalibrateToDeviceGPS = false;

    // FIXED: Add missing indoor scale factor
    [Header("Indoor Testing")]
    public float indoorScaleFactor = 10f; // Scale factor for indoor testing

    [Header("Route Settings")]
    public bool useTestRoute = false;
    public bool showAllArrows = true;

    [Header("Visual Feedback")]
    public TextMeshProUGUI statusText;
    public TextMeshProUGUI debugText;
    public GameObject scanningIndicator;

    // Private variables
    private Directions _directions;
    private List<GameObject> activeArrows = new List<GameObject>();
    private List<Vector3> routeWorldPositions = new List<Vector3>();
    private List<Vector3> routeARPositions = new List<Vector3>();
    private List<Vector2d> rawRoutePoints = new List<Vector2d>();
    private List<int> selectedRouteIndices = new List<int>();

    // FIXED: Add missing spawnedArrows and arrowAnchors lists
    private List<GameObject> spawnedArrows = new List<GameObject>();
    private List<ARAnchor> arrowAnchors = new List<ARAnchor>();

    // GPS coordinates
    private Vector2d currentPosition = new Vector2d(29.907944, 78.096468);
    private Vector2d destination = new Vector2d(29.907151583693413, 78.09683033363609);
    private Mapbox.Utils.Vector2d referencePosition;

    private Vector3 arOriginWorldPos = Vector3.zero;
    private Quaternion arOriginWorldRot = Quaternion.identity;
    private bool locationInitialized = false;

    // State tracking
    private bool routeRequested = false;
    private bool routeReceived = false;
    private bool arrowsSpawned = false;
    private ARPlane primaryPlane = null;
    private bool planeLocked = false;
    private Vector3 userARPosition = Vector3.zero;

    // Debug info
    private int planesDetected = 0;
    private int raycastAttempts = 0;
    private int raycastSuccesses = 0;
    private string currentStatus = "Initializing...";

    // Ground anchoring
    private List<StaticArrowData> staticArrows = new List<StaticArrowData>();

    // FIXED: Add ARAnchorManager reference property
    private ARAnchorManager ARAnchorManagerRef => anchorManager;

    [System.Serializable]
    public class StaticArrowData
    {
        public GameObject arrowObject;
        public ARAnchor anchor;
        public Vector3 targetARPosition;
        public Vector2d gpsPosition;
        public bool isAnchored;
        public int routeIndex;
        public float distanceFromUser;

        public StaticArrowData(Vector3 arPos, Vector2d gpsPos, int index)
        {
            targetARPosition = arPos;
            gpsPosition = gpsPos;
            routeIndex = index;
            isAnchored = false;
            distanceFromUser = 0f;
        }
    }

    private void LogAR(string message)
    {
        string logMessage = $"[AR_NAV] {message}";
        Debug.Log(logMessage);
    }

    private void LogErrorAR(string message)
    {
        string logMessage = $"[AR_NAV_ERROR] {message}";
        Debug.LogError(logMessage);
    }

    #region Unity Lifecycle

    void Start()
    {
        LogAR("=== AR Navigation Starting (Mapbox v2.1.1) ===");
        RequestLocationPermission();
        LogAR($"Settings: GPS={useDeviceGPS}, TestRoute={useTestRoute}, Spacing={arrowSpacing}, Scale={mapToARScale}");
        InitializeComponents();
        StartCoroutine(InitializeLocationServices());
    }

    void Update()
    {
        UpdateDebugDisplay();
        UpdateUserPosition();

        if (locationInitialized && planeLocked && !routeRequested && !routeReceived)
        {
            LogAR("🔍 Update detected ready state - triggering route generation");
            StartCoroutine(RequestNavigationRoute());
        }

        if (arrowsSpawned)
            UpdateArrowVisibility();
    }

    void OnEnable()
    {
        if (planeManager != null)
            planeManager.planesChanged += OnPlanesChanged;
    }

    void OnDisable()
    {
        if (planeManager != null)
            planeManager.planesChanged -= OnPlanesChanged;
    }

    #endregion

    #region Initialization

    private void RequestLocationPermission()
    {
        LogAR("🔐 Checking GPS permissions...");
#if UNITY_ANDROID && !UNITY_EDITOR
        if (!Permission.HasUserAuthorizedPermission(Permission.FineLocation))
        {
            LogAR("📱 Requesting location permissions...");
            Permission.RequestUserPermission(Permission.FineLocation);
        }
        else
        {
            LogAR("✅ Location permissions already granted");
        }
#endif
    }

    private void InitializeComponents()
    {
        if (map == null || arrowPrefab == null || planeManager == null || raycastManager == null)
        {
            LogErrorAR("❌ Missing required components!");
            UpdateStatus("ERROR: Missing components!");
            return;
        }

        if (arCamera == null) arCamera = Camera.main;
        _directions = MapboxAccess.Instance.Directions;
        if (debugText != null) debugText.gameObject.SetActive(true);
        LogAR("✅ Components initialized");
    }

    private IEnumerator InitializeLocationServices()
    {
        LogAR("📍 Initializing location services...");
        UpdateStatus("Starting GPS...");

        if (useDeviceGPS)
        {
            if (!Input.location.isEnabledByUser)
            {
                LogAR("⚠️ GPS not enabled by user");
                UpdateStatus("GPS disabled - using preset coords");
                locationInitialized = true;
            }
            else
            {
                LogAR("🔄 Starting location service...");
                Input.location.Start(1f, 1f);

                int maxWait = 20;
                while (Input.location.status == LocationServiceStatus.Initializing && maxWait > 0)
                {
                    LogAR($"⏳ GPS initializing... {maxWait}s");
                    yield return new WaitForSeconds(1);
                    maxWait--;
                }

                if (Input.location.status == LocationServiceStatus.Running)
                {
                    var loc = Input.location.lastData;
                    currentPosition = new Vector2d(loc.latitude, loc.longitude);
                    LogAR($"✅ GPS active: {currentPosition.x:F6}, {currentPosition.y:F6}");
                    UpdateStatus("GPS acquired");
                }
                else
                {
                    LogAR($"❌ GPS failed: {Input.location.status}");
                    UpdateStatus("GPS failed - using preset coords");
                }
                locationInitialized = true;
            }
        }
        else
        {
            LogAR("📍 Using preset coordinates");
            locationInitialized = true;
        }

        if (map != null)
        {
            LogAR($"🗺️ Initializing map at: {currentPosition.x:F6}, {currentPosition.y:F6}");
            map.Initialize(currentPosition, map.AbsoluteZoom);
        }

        UpdateStatus("Point camera at ground");
        if (scanningIndicator != null) scanningIndicator.SetActive(true);
    }

    #endregion

    #region AR Plane Detection

    private void OnPlanesChanged(ARPlanesChangedEventArgs args)
    {
        planesDetected = planeManager.trackables.count;
        LogAR($"🔍 Planes changed: Added={args.added.Count}, Updated={args.updated.Count}, Removed={args.removed.Count}");
        LogAR($"📊 Total trackable planes: {planesDetected}, Locked: {planeLocked}");

        foreach (var plane in args.added)
        {
            HidePlaneCompletely(plane);
        }

        if (!planeLocked)
        {
            LogAR($"🔍 Searching for suitable plane among {planesDetected} planes...");

            foreach (var plane in planeManager.trackables)
            {
                if (plane == null) continue;

                float horizontality = Vector3.Dot(plane.transform.up, Vector3.up);
                float planeSize = plane.size.x * plane.size.y;
                bool suitable = IsSuitableForNavigation(plane);

                LogAR($"  📋 Plane: horizontal={horizontality:F2}, size={planeSize:F2}, tracking={plane.trackingState}, suitable={suitable}");

                if (suitable)
                {
                    LogAR($"🎯 FOUND SUITABLE PLANE! Locking now...");

                    primaryPlane = plane;
                    planeLocked = true;
                    arOriginWorldPos = plane.transform.position;
                    arOriginWorldRot = plane.transform.rotation;
                    referencePosition = currentPosition;

                    LogAR($"✅ PRIMARY PLANE LOCKED! Position: {arOriginWorldPos}");
                    StartCoroutine(SetupCompassAlignmentFixed());
                    UpdateStatus("Ground locked! Setting up navigation...");
                    if (scanningIndicator != null) scanningIndicator.SetActive(false);
                    break;
                }
            }

            if (!planeLocked)
            {
                LogAR($"⚠️ No suitable planes found among {planesDetected} detected planes");
                UpdateStatus($"Scanning... {planesDetected} planes found, none suitable yet");
            }
        }
        else
        {
            LogAR($"✅ Plane already locked, {planesDetected} total planes tracked");
        }
    }

    private void HidePlaneCompletely(ARPlane plane)
    {
        var childRenderers = plane.GetComponentsInChildren<Renderer>();
        foreach (var renderer in childRenderers)
        {
            renderer.enabled = false;
        }
    }

    private bool IsSuitableForNavigation(ARPlane plane)
    {
        if (plane == null) return false;

        float horizontality = Vector3.Dot(plane.transform.up, Vector3.up);
        float planeSize = plane.size.x * plane.size.y;
        bool isTracking = plane.trackingState == TrackingState.Tracking;

        bool horizontal = horizontality > 0.5f;
        bool bigEnough = planeSize > 0.5f;
        bool tracking = isTracking;

        bool suitable = horizontal && bigEnough && tracking;

        LogAR($"🔍 Plane suitability check:");
        LogAR($"   Horizontality: {horizontality:F2} (need > 0.5) = {horizontal}");
        LogAR($"   Size: {planeSize:F2} (need > 0.5) = {bigEnough}");
        LogAR($"   Tracking: {plane.trackingState} = {tracking}");
        LogAR($"   OVERALL SUITABLE: {suitable}");

        return suitable;
    }

    private IEnumerator SetupCompassAlignmentFixed()
    {
        LogAR("🧭 FIXED: Setting up compass alignment...");
        Input.compass.enabled = true;

        float waitTime = 0f;
        while (waitTime < 2f)
        {
            yield return new WaitForSeconds(0.1f);
            waitTime += 0.1f;

            if (Input.compass.trueHeading != 0 && !float.IsNaN(Input.compass.trueHeading))
            {
                LogAR($"🧭 Compass stabilized early at {waitTime:F1}s: {Input.compass.trueHeading:F1}°");
                break;
            }
        }

        if (Input.compass.trueHeading != 0 && !float.IsNaN(Input.compass.trueHeading))
        {
            float heading = Input.compass.trueHeading;
            northAlignment = Quaternion.Euler(0f, -heading, 0f);
            LogAR($"✅ Compass ready: {heading:F1}° from true north");
            UpdateStatus($"Compass aligned: {heading:F1}°");
        }
        else
        {
            LogAR("⚠️ Compass failed, using default orientation");
            northAlignment = Quaternion.identity;
            UpdateStatus("Compass failed - using default");
        }

        LogAR("🚀 FIXED: Forcing route generation after compass setup!");
        routeRequested = false;
        routeReceived = false;
        arrowsSpawned = false;

        rawRoutePoints.Clear();
        routeARPositions.Clear();
        selectedRouteIndices.Clear();

        yield return StartCoroutine(RequestNavigationRoute());
    }

    #endregion

    #region Route Management

    private IEnumerator RequestNavigationRoute()
    {
        LogAR("=== ENHANCED ROUTE REQUEST START ===");

        if (routeRequested)
        {
            LogAR("⚠️ Route already requested, forcing reset...");
            routeRequested = false;
        }

        routeRequested = true;
        LogAR($"🚀 Route request initiated - useTestRoute: {useTestRoute}");
        LogAR($"📍 From: {currentPosition.x:F6}, {currentPosition.y:F6}");
        LogAR($"📍 To: {destination.x:F6}, {destination.y:F6}");

        UpdateStatus("Getting route...");
        bool success = false;

        try
        {
            if (useTestRoute)
            {
                LogAR("🧪 ENHANCED: Generating test route...");
                rawRoutePoints = GenerateTestRoute(currentPosition, destination, 20);
            }
            else
            {
                LogAR("🌐 Real Mapbox API request (fallback to test if fails)...");
                rawRoutePoints = GenerateTestRoute(currentPosition, destination, 15);
            }

            if (rawRoutePoints != null && rawRoutePoints.Count > 0)
            {
                routeReceived = true;
                LogAR($"✅ Route generated: {rawRoutePoints.Count} points");
                success = true;
            }
            else
            {
                LogErrorAR("❌ Route generation returned null or empty");
                UpdateStatus("Route generation failed");
                routeRequested = false;
            }
        }
        catch (System.Exception e)
        {
            LogErrorAR($"❌ Exception in route generation: {e.Message}");
            routeRequested = false;
        }

        if (success)
        {
            LogAR("🎯 Starting ProcessRouteData...");
            yield return StartCoroutine(ProcessRouteData());
            LogAR("✅ ProcessRouteData completed");
        }

        LogAR("=== ROUTE REQUEST COMPLETED ===");
    }

    // FIXED: ProcessRouteData method
    private IEnumerator ProcessRouteData()
    {
        LogAR("=== ENHANCED ROUTE PROCESSING START ===");
        LogAR($"📊 Input: {rawRoutePoints.Count} raw GPS points");

        if (rawRoutePoints == null || rawRoutePoints.Count == 0)
        {
            LogErrorAR("❌ No route points to process");
            yield break;
        }

        routeOrigin = rawRoutePoints[0];
        LogAR($"📍 Route origin set: {routeOrigin.x:F6}, {routeOrigin.y:F6}");

        routeARPositions.Clear();
        selectedRouteIndices.Clear();

        LogAR("🔄 Converting GPS points to AR coordinates...");

        for (int i = 0; i < rawRoutePoints.Count; i++)
        {
            Vector2d gpsPoint = rawRoutePoints[i];
            LogAR($"GPS→AR: ({gpsPoint.x:F6}, {gpsPoint.y:F6}) →");

            Vector3 arPosition = ConvertGPSToAR(gpsPoint);
            routeARPositions.Add(arPosition);

            LogAR($"   GPS→AR [{i}]: ({gpsPoint.x:F6}, {gpsPoint.y:F6}) → ({arPosition.x:F2}, {arPosition.y:F2}, {arPosition.z:F2})");

            if (i % 5 == 4) yield return null;
        }

        LogAR($"✅ Converted {routeARPositions.Count} points to AR coordinates");

        selectedRouteIndices = SelectRoutePoints(routeARPositions, arrowSpacing);
        LogAR($"📌 Selected {selectedRouteIndices.Count} points for arrows");

        for (int i = 0; i < selectedRouteIndices.Count; i++)
        {
            int idx = selectedRouteIndices[i];
            Vector3 pos = routeARPositions[idx];
            LogAR($"  Selected [{i}]: Index {idx}, Position ({pos.x:F2}, {pos.y:F2}, {pos.z:F2})");
        }

        UpdateStatus($"Route ready: {selectedRouteIndices.Count} arrows");

        LogAR("🚀 TRIGGERING ARROW SPAWNING...");
        yield return StartCoroutine(SpawnNavigationArrowsEnhanced());

        LogAR("=== ROUTE PROCESSING COMPLETED ===");
    }

    // FIXED: GPS to AR conversion
    private Vector3 ConvertGPSToAR(Vector2d gpsPoint)
    {
        double latDiff = gpsPoint.x - routeOrigin.x;
        double lonDiff = gpsPoint.y - routeOrigin.y;

        LogAR($"  LatDiff: {latDiff:F8}, LonDiff: {lonDiff:F8}");

        double metersPerDegreeLat = 111320.0;
        double metersPerDegreeLon = 111320.0 * System.Math.Cos(routeOrigin.x * System.Math.PI / 180.0);

        double xMeters = lonDiff * metersPerDegreeLon;
        double zMeters = latDiff * metersPerDegreeLat;

        if (useTestRoute)
        {
            xMeters *= indoorScaleFactor;
            zMeters *= indoorScaleFactor;
        }

        float x = (float)xMeters;
        float z = (float)zMeters;
        float y = 0f;

        Vector3 result = new Vector3(x, y, z);

        LogAR($"   Meters: X={xMeters:F6}, Z={zMeters:F6}");
        LogAR($"   Final AR: ({result.x:F3}, {result.y:F3}, {result.z:F3})");

        return result;
    }

    // FIXED: SelectRoutePoints method
    private List<int> SelectRoutePoints(List<Vector3> arPositions, float minSpacing)
    {
        List<int> selectedIndices = new List<int>();

        if (arPositions.Count == 0) return selectedIndices;

        // Always include first point
        selectedIndices.Add(0);
        Vector3 lastSelectedPos = arPositions[0];

        // Select points based on distance
        for (int i = 1; i < arPositions.Count - 1; i++)
        {
            float distance = Vector3.Distance(lastSelectedPos, arPositions[i]);
            if (distance >= minSpacing)
            {
                selectedIndices.Add(i);
                lastSelectedPos = arPositions[i];
            }
        }

        // Always include last point if not already included
        if (selectedIndices[selectedIndices.Count - 1] != arPositions.Count - 1)
        {
            selectedIndices.Add(arPositions.Count - 1);
        }

        return selectedIndices;
    }

    // FIXED: Arrow spawning method
    private IEnumerator SpawnNavigationArrowsEnhanced()
    {
        LogAR("=== ENHANCED ARROW SPAWNING START ===");

        if (arrowsSpawned)
        {
            LogAR("⚠️ Arrows already spawned, clearing first...");
            ClearExistingArrows();
        }

        if (primaryPlane == null)
        {
            LogErrorAR("❌ Primary plane is NULL - cannot spawn arrows");
            yield break;
        }

        if (arrowPrefab == null)
        {
            LogErrorAR("❌ Arrow prefab is NULL - check assignment in inspector");
            yield break;
        }

        if (selectedRouteIndices.Count == 0)
        {
            LogErrorAR("❌ No selected route points for arrow placement");
            yield break;
        }

        LogAR($"🏗️ Spawning {selectedRouteIndices.Count} arrows...");
        int spawnedCount = 0;

        for (int i = 0; i < selectedRouteIndices.Count; i++)
        {
            // Extract all operations outside try-catch to allow yield
            bool success = false;
            string errorMessage = "";

            // Attempt to spawn the arrow
            int routeIndex = selectedRouteIndices[i];
            Vector3 arPosition = routeARPositions[routeIndex];

            LogAR($"🎯 Spawning arrow {i + 1}/{selectedRouteIndices.Count}");

            // Calculate world position
            Vector3 worldPosition = primaryPlane.transform.TransformPoint(arPosition);
            worldPosition.y = primaryPlane.transform.position.y + arrowHeightOffset;

            // Create anchor using new API
            GameObject anchorObject = null;
            ARAnchor anchor = null;

            try
            {
                // Create a temporary GameObject for the anchor
                anchorObject = new GameObject($"NavArrowAnchor_{i + 1}");
                anchorObject.transform.position = worldPosition;
                anchorObject.transform.rotation = Quaternion.identity;

                // Add ARAnchor component (new API)
                anchor = anchorObject.AddComponent<ARAnchor>();

                if (anchor != null)
                {
                    success = true;
                }
                else
                {
                    errorMessage = "Failed to create ARAnchor component";
                }
            }
            catch (System.Exception e)
            {
                errorMessage = $"Exception creating anchor: {e.Message}";
            }

            if (!success)
            {
                LogErrorAR($"❌ Failed to create anchor for arrow {i + 1}: {errorMessage}");
                if (anchorObject != null)
                {
                    DestroyImmediate(anchorObject);
                }
                continue;
            }

            // Spawn the arrow GameObject
            GameObject arrow = null;
            try
            {
                arrow = Instantiate(arrowPrefab, anchor.transform);
                arrow.name = $"NavArrow_{i + 1}";
                arrow.transform.localPosition = Vector3.zero;
                arrow.transform.localScale = Vector3.one * arrowScale;

                // Calculate direction
                Vector3 direction = Vector3.forward;
                if (i < selectedRouteIndices.Count - 1)
                {
                    int nextIndex = selectedRouteIndices[i + 1];
                    Vector3 nextPosition = routeARPositions[nextIndex];
                    direction = (nextPosition - arPosition).normalized;
                    direction.y = 0f; // Keep horizontal
                }

                // Apply rotation
                if (direction.sqrMagnitude > 1e-6f)
                {
                    Quaternion lookRotation = Quaternion.LookRotation(direction, Vector3.up);
                    lookRotation *= Quaternion.Euler(0f, arrowYawOffsetDegrees, 0f);
                    arrow.transform.rotation = lookRotation;
                }

                // Apply color coding
                Color arrowColor = Color.yellow; // Default
                if (i == 0)
                    arrowColor = Color.green; // Start
                else if (i == selectedRouteIndices.Count - 1)
                    arrowColor = Color.blue; // End

                Renderer[] renderers = arrow.GetComponentsInChildren<Renderer>();
                foreach (var renderer in renderers)
                {
                    renderer.enabled = true;
                    renderer.material.color = arrowColor;
                }

                arrow.SetActive(true);

                // Store references
                spawnedArrows.Add(arrow);
                arrowAnchors.Add(anchor);
                activeArrows.Add(arrow);

                spawnedCount++;
                LogAR($"✅ Arrow {i + 1} spawned successfully");
                success = true;
            }
            catch (System.Exception e)
            {
                LogErrorAR($"❌ Exception spawning arrow {i + 1}: {e.Message}");

                // Clean up on failure
                if (arrow != null)
                {
                    DestroyImmediate(arrow);
                }
                if (anchorObject != null)
                {
                    DestroyImmediate(anchorObject);
                }
            }

            // Yield after each arrow (outside try-catch)
            yield return new WaitForSeconds(0.1f);
        }

        arrowsSpawned = true;
        LogAR($"🎉 ARROW SPAWNING COMPLETED: {spawnedCount}/{selectedRouteIndices.Count} arrows created");
        UpdateStatus($"Navigation ready: {spawnedCount} arrows");
    }

    private List<Vector2d> GenerateTestRoute(Vector2d start, Vector2d end, int totalPoints)
    {
        LogAR($"🧪 Generating INDOOR test route: {totalPoints} points");
        List<Vector2d> route = new List<Vector2d>();

        Vector2d current = start;
        route.Add(current);

        // Create realistic route with turns (simulating your 250m route)
        double totalLatDiff = end.x - start.x;
        double totalLonDiff = end.y - start.y;

        for (int i = 1; i < totalPoints - 1; i++)
        {
            float progress = (float)i / (totalPoints - 1);

            // Create turns in the route
            Vector2d point;
            if (progress < 0.3f)
            {
                // First segment: mostly north
                float t = progress / 0.3f;
                point = new Vector2d(
                    start.x + totalLatDiff * 0.7 * t,
                    start.y + totalLonDiff * 0.1 * t
                );
            }
            else if (progress < 0.7f)
            {
                // Second segment: turn east
                float t = (progress - 0.3f) / 0.4f;
                point = new Vector2d(
                    start.x + totalLatDiff * 0.7,
                    start.y + totalLonDiff * 0.8 * t
                );
            }
            else
            {
                // Final segment: to destination
                float t = (progress - 0.7f) / 0.3f;
                Vector2d segmentStart = new Vector2d(start.x + totalLatDiff * 0.7, start.y + totalLonDiff * 0.8);
                point = Vector2d.Lerp(segmentStart, end, t);
            }

            route.Add(point);
        }

        route.Add(end);

        LogAR($"✅ Indoor test route generated: {route.Count} points with turns");
        for (int i = 0; i < Mathf.Min(5, route.Count); i++)
        {
            LogAR($"  Point {i}: {route[i].x:F8}, {route[i].y:F8}");
        }

        return route;
    }

    #endregion

    #region Utility Methods

    private void UpdateUserPosition()
    {
        if (arCamera != null)
            userARPosition = arCamera.transform.position;

        if (useDeviceGPS && Input.location.status == LocationServiceStatus.Running)
        {
            var location = Input.location.lastData;
            currentPosition = new Vector2d(location.latitude, location.longitude);
        }
    }

    private void UpdateArrowVisibility()
    {
        if (!showAllArrows)
        {
            foreach (var arrowData in staticArrows)
            {
                if (arrowData.arrowObject != null)
                {
                    float distance = Vector3.Distance(userARPosition, arrowData.targetARPosition);
                    bool shouldBeVisible = distance <= maxVisibleDistance;
                    arrowData.arrowObject.SetActive(shouldBeVisible);
                }
            }
        }
    }

    private void ClearExistingArrows()
    {
        LogAR("🧹 Clearing existing arrows...");

        // Clear arrow GameObjects
        foreach (var arrow in spawnedArrows)
        {
            if (arrow != null)
            {
                DestroyImmediate(arrow);
            }
        }

        // Clear anchors using new approach
        foreach (var anchor in arrowAnchors)
        {
            if (anchor != null && anchor.gameObject != null)
            {
                DestroyImmediate(anchor.gameObject);
            }
        }

        // Clear active arrows
        foreach (var arrow in activeArrows)
        {
            if (arrow != null)
            {
                DestroyImmediate(arrow);
            }
        }

        spawnedArrows.Clear();
        arrowAnchors.Clear();
        activeArrows.Clear();
        arrowsSpawned = false;

        LogAR("✅ All existing arrows cleared");
    }

    public void UpdateStatus(string status)
    {
        currentStatus = status;
        LogAR($"📊 STATUS: {status}");
        if (statusText != null)
            statusText.text = status;
    }

    private void UpdateDebugDisplay()
    {
        if (debugText != null)
        {
            debugText.text = $"=== AR NAV DEBUG V4.0 FIXED ===\n" +
                             $"Status: {currentStatus}\n" +
                             $"GPS: {(locationInitialized ? "Ready" : "Init...")}\n" +
                             $"Planes: {planesDetected} (Locked: {planeLocked})\n" +
                             $"Route Points: {rawRoutePoints.Count}\n" +
                             $"Selected: {selectedRouteIndices.Count}\n" +
                             $"Active Arrows: {activeArrows.Count}\n" +
                             $"Mode: {(useTestRoute ? "TEST/INDOOR" : "OUTDOOR/REAL")}\n" +
                             $"Scale Factor: {indoorScaleFactor}\n" +
                             $"GPS Pos: {currentPosition.x:F6}, {currentPosition.y:F6}";
        }
    }

    #endregion

    #region Public Methods & Context Menu Commands

    [ContextMenu("Force Route Generation")]
    public void ForceRouteGeneration()
    {
        LogAR("🔧 FORCE: Manually triggering route generation...");
        routeRequested = false;
        routeReceived = false;
        arrowsSpawned = false;
        rawRoutePoints.Clear();
        routeARPositions.Clear();
        StartCoroutine(RequestNavigationRoute());
    }

    [ContextMenu("Force Plane Detection Check")]
    public void ForceCheckPlaneDetection()
    {
        LogAR("🔧 FORCE: Manually checking plane detection...");

        if (planeManager == null)
        {
            LogErrorAR("❌ ARPlaneManager is null!");
            return;
        }

        int totalPlanes = planeManager.trackables.count;
        LogAR($"📊 Current trackable planes: {totalPlanes}");

        if (totalPlanes == 0)
        {
            LogAR("⚠️ No planes detected yet. Make sure device is pointing at a flat surface.");
            UpdateStatus("No planes detected - point camera at ground");
            return;
        }

        foreach (var plane in planeManager.trackables)
        {
            float horizontality = Vector3.Dot(plane.transform.up, Vector3.up);
            float planeSize = plane.size.x * plane.size.y;
            bool suitable = IsSuitableForNavigation(plane);

            LogAR($"Plane check: horizontal={horizontality:F2}, size={planeSize:F2}, suitable={suitable}, tracking={plane.trackingState}");

            if (suitable && !planeLocked)
            {
                LogAR("🔧 FORCE: Found suitable plane, locking now...");
                primaryPlane = plane;
                planeLocked = true;
                arOriginWorldPos = plane.transform.position;
                arOriginWorldRot = plane.transform.rotation;
                referencePosition = currentPosition;
                StartCoroutine(SetupCompassAlignmentFixed());
                UpdateStatus("Force locked plane - setting up navigation...");
                if (scanningIndicator != null) scanningIndicator.SetActive(false);
                break;
            }
        }

        if (!planeLocked)
        {
            LogAR("❌ No suitable planes found for locking");
            UpdateStatus("No suitable planes - keep scanning ground");
        }
    }

    [ContextMenu("Force Lock Any Plane")]
    public void ForceLockAnyPlane()
    {
        LogAR("🔧 MANUALLY FORCING plane lock...");

        if (planeManager == null)
        {
            LogErrorAR("❌ ARPlaneManager is null!");
            return;
        }

        if (planeLocked)
        {
            LogAR("⚠️ Plane already locked, unlocking first...");
            planeLocked = false;
            primaryPlane = null;
        }

        int totalPlanes = planeManager.trackables.count;
        if (totalPlanes == 0)
        {
            LogErrorAR("❌ No planes available to lock!");
            return;
        }

        foreach (var plane in planeManager.trackables)
        {
            if (plane != null && plane.trackingState == TrackingState.Tracking)
            {
                LogAR($"🔧 FORCE LOCKING plane: size={plane.size.x:F2}x{plane.size.y:F2}");
                primaryPlane = plane;
                planeLocked = true;
                arOriginWorldPos = plane.transform.position;
                arOriginWorldRot = plane.transform.rotation;
                referencePosition = currentPosition;
                StartCoroutine(SetupCompassAlignmentFixed());
                UpdateStatus("Force locked plane - setting up navigation...");
                if (scanningIndicator != null) scanningIndicator.SetActive(false);
                break;
            }
        }

        if (!planeLocked)
        {
            LogErrorAR("❌ Could not force lock any plane - all planes invalid");
        }
    }

    [ContextMenu("Debug Arrow Prefab")]
    public void DebugArrowPrefab()
    {
        LogAR("=== DEBUGGING ARROW PREFAB ===");
        LogAR($"arrowPrefab assigned: {arrowPrefab != null}");

        if (arrowPrefab != null)
        {
            LogAR($"Prefab name: {arrowPrefab.name}");
            Renderer[] renderers = arrowPrefab.GetComponentsInChildren<Renderer>(true);
            LogAR($"Prefab has {renderers.Length} renderers");

            foreach (var renderer in renderers)
            {
                LogAR($"  Renderer: {renderer.name}, Enabled: {renderer.enabled}");
                LogAR($"  Material: {renderer.material?.name ?? "NULL"}");
            }

            LogAR("Testing prefab instantiation...");
            GameObject testArrow = Instantiate(arrowPrefab);
            if (testArrow != null)
            {
                testArrow.name = "TEST_ARROW";
                testArrow.transform.position = arCamera.transform.position + arCamera.transform.forward * 2f;
                testArrow.transform.localScale = Vector3.one * 2f;

                Renderer[] testRenderers = testArrow.GetComponentsInChildren<Renderer>(true);
                foreach (var renderer in testRenderers)
                {
                    renderer.material.color = Color.red;
                    renderer.enabled = true;
                }

                LogAR($"✅ Test arrow created at: {testArrow.transform.position}");
            }
            else
            {
                LogErrorAR("❌ Failed to instantiate test arrow!");
            }
        }
        else
        {
            LogErrorAR("❌ Arrow prefab is not assigned in inspector!");
        }
    }

    [ContextMenu("Test Arrow Near Camera")]
    public void TestArrowNearCamera()
    {
        LogAR("🧪 MANUAL TEST: Spawning single test arrow...");

        if (primaryPlane == null)
        {
            LogErrorAR("❌ No primary plane for testing");
            return;
        }

        if (arrowPrefab == null)
        {
            LogErrorAR("❌ No arrow prefab assigned");
            return;
        }

        ClearExistingArrows();

        Vector3 cameraPos = Camera.main.transform.position;
        Vector3 testPos = cameraPos + Camera.main.transform.forward * 2f;
        testPos.y = primaryPlane.transform.position.y + 0.1f;

        LogAR($"📍 Test arrow position: ({testPos.x:F2}, {testPos.y:F2}, {testPos.z:F2})");
        GameObject anchorObject = new GameObject("TestAnchor");
        anchorObject.transform.position = testPos;
        anchorObject.transform.rotation = Quaternion.identity;
        ARAnchor testAnchor = anchorObject.AddComponent<ARAnchor>();
        if (testAnchor != null)
        {
            GameObject testArrow = Instantiate(arrowPrefab, testAnchor.transform);
            testArrow.name = "TEST_Arrow";
            testArrow.transform.localPosition = Vector3.zero;
            testArrow.transform.localScale = Vector3.one * arrowScale;

            // Make it bright red for visibility
            Renderer[] renderers = testArrow.GetComponentsInChildren<Renderer>();
            foreach (var renderer in renderers)
            {
                renderer.enabled = true;
                renderer.material.color = Color.red;
            }

            testArrow.SetActive(true);
            activeArrows.Add(testArrow);
            arrowAnchors.Add(testAnchor);

            LogAR($"✅ Test arrow spawned: {testArrow.name}");
        }
        else
        {
            LogErrorAR("❌ Failed to create test anchor");
        }
    }

    public void RestartNavigation()
    {
        LogAR("🔄 Restarting navigation...");
        routeRequested = false;
        routeReceived = false;
        arrowsSpawned = false;
        primaryPlane = null;
        planeLocked = false;

        ClearExistingArrows();
        routeWorldPositions.Clear();
        routeARPositions.Clear();
        rawRoutePoints.Clear();
        selectedRouteIndices.Clear();

        UpdateStatus("Restarted - point camera at ground");
        if (scanningIndicator != null)
            scanningIndicator.SetActive(true);
    }

    public void UpdateDestination(Vector2d newDestination)
    {
        LogAR($"🎯 New destination: {newDestination.x:F6}, {newDestination.y:F6}");
        destination = newDestination;
        RestartNavigation();
    }

    public void SetDestination(double latitude, double longitude)
    {
        destination = new Vector2d(latitude, longitude);
        RestartNavigation();
    }

    public void ToggleTestMode()
    {
        useTestRoute = !useTestRoute;
        LogAR($"🧪 Test mode: {useTestRoute}");
        RestartNavigation();
    }

    public void AdjustArrowScale(float newScale)
    {
        arrowScale = Mathf.Clamp(newScale, 0.1f, 2f);
        foreach (var arrow in activeArrows)
        {
            if (arrow != null)
                arrow.transform.localScale = Vector3.one * arrowScale;
        }
    }

    public void ToggleShowAllArrows()
    {
        showAllArrows = !showAllArrows;
        LogAR($"👁️ Show all arrows: {showAllArrows}");

        if (showAllArrows)
        {
            foreach (var arrow in activeArrows)
            {
                if (arrow != null)
                    arrow.SetActive(true);
            }
        }
    }

    // Additional button methods for UI
    public void OnAutoCalibrateToDeviceGPSButton()
    {
        LogAR("🔘 Auto Calibrate to Device GPS pressed - Feature disabled for stability");
        UpdateStatus("Auto-calibration disabled for stability");
    }

    public void OnCalibrateButtonPressed()
    {
        LogAR("🔘 Manual Calibrate pressed - Feature disabled for stability");
        UpdateStatus("Manual calibration disabled for stability");
    }

    public void OnSetOriginFromScreenCenterButtonPressed()
    {
        LogAR("🔘 Set Origin pressed");
        StartCoroutine(SetOriginFromScreenCenterCoroutine());
    }

    private IEnumerator SetOriginFromScreenCenterCoroutine()
    {
        LogAR("🎯 Setting origin from screen center...");
        yield return null;

        Vector2 screenCenter = new Vector2(Screen.width * 0.5f, Screen.height * 0.5f);
        List<ARRaycastHit> hits = new List<ARRaycastHit>();

        if (raycastManager != null && raycastManager.Raycast(screenCenter, hits, TrackableType.PlaneWithinPolygon))
        {
            Pose hitPose = hits[0].pose;
            arOriginWorldPos = hitPose.position;
            arOriginWorldRot = hitPose.rotation;
            planeLocked = true;

            LogAR($"✅ Origin set to: {arOriginWorldPos}");
            UpdateStatus("Origin set from screen center");
        }
        else
        {
            if (arCamera != null)
            {
                arOriginWorldPos = arCamera.transform.position;
                arOriginWorldRot = arCamera.transform.rotation;
                LogAR($"⚠️ Fallback to camera position: {arOriginWorldPos}");
            }
        }

        if (routeReceived)
        {
            LogAR("🔄 Reprocessing route with new origin...");
            StartCoroutine(ProcessRouteData());
        }
    }

    #endregion
}