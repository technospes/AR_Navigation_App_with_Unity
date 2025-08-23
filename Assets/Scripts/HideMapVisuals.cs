using UnityEngine;
using Mapbox.Unity.Map;

public class HideMapVisuals : MonoBehaviour
{
    [Header("Map Reference")]
    public AbstractMap mapboxMap;

    [Header("Options")]
    public bool hideOnStart = true;
    public bool keepColliders = true; // Keep colliders for raycasting if needed

    void Start()
    {
        if (hideOnStart && mapboxMap != null)
        {
            HideMapMesh();
        }
    }

    public void HideMapMesh()
    {
        if (mapboxMap == null)
        {
            Debug.LogError("Mapbox map reference not assigned!");
            return;
        }

        // Method 1: Disable all MeshRenderer components
        MeshRenderer[] renderers = mapboxMap.GetComponentsInChildren<MeshRenderer>();
        foreach (var renderer in renderers)
        {
            renderer.enabled = false;
        }

        // Method 2: Also disable SkinnedMeshRenderer if any
        SkinnedMeshRenderer[] skinnedRenderers = mapboxMap.GetComponentsInChildren<SkinnedMeshRenderer>();
        foreach (var renderer in skinnedRenderers)
        {
            renderer.enabled = false;
        }

        // Optional: Keep colliders for functionality but hide visuals
        if (!keepColliders)
        {
            Collider[] colliders = mapboxMap.GetComponentsInChildren<Collider>();
            foreach (var collider in colliders)
            {
                collider.enabled = false;
            }
        }

        Debug.Log("Map visuals hidden successfully");
    }

    public void ShowMapMesh()
    {
        if (mapboxMap == null) return;

        MeshRenderer[] renderers = mapboxMap.GetComponentsInChildren<MeshRenderer>();
        foreach (var renderer in renderers)
        {
            renderer.enabled = true;
        }

        SkinnedMeshRenderer[] skinnedRenderers = mapboxMap.GetComponentsInChildren<SkinnedMeshRenderer>();
        foreach (var renderer in skinnedRenderers)
        {
            renderer.enabled = true;
        }

        if (!keepColliders)
        {
            Collider[] colliders = mapboxMap.GetComponentsInChildren<Collider>();
            foreach (var collider in colliders)
            {
                collider.enabled = true;
            }
        }

        Debug.Log("Map visuals restored");
    }

    public void ToggleMapVisibility()
    {
        if (mapboxMap == null) return;

        MeshRenderer firstRenderer = mapboxMap.GetComponentInChildren<MeshRenderer>();
        if (firstRenderer != null)
        {
            if (firstRenderer.enabled)
                HideMapMesh();
            else
                ShowMapMesh();
        }
    }
}