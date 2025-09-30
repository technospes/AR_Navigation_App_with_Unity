using System.Collections.Generic;
using UnityEngine;

public class ArrowPool
{
    private Queue<GameObject> pool = new Queue<GameObject>();
    private GameObject arrowPrefab;
    private Transform poolParent; // A parent object to hold inactive arrows

    // Constructor
    public ArrowPool(GameObject prefab, int initialSize)
    {
        this.arrowPrefab = prefab;

        // Create a parent object in the scene to keep things tidy
        poolParent = new GameObject("ArrowPool").transform;

        for (int i = 0; i < initialSize; i++)
        {
            GameObject arrow = GameObject.Instantiate(arrowPrefab, poolParent);
            arrow.SetActive(false);
            pool.Enqueue(arrow);
        }
    }
    public GameObject Get()
    {
        if (pool.Count > 0)
        {
            GameObject arrow = pool.Dequeue();
            // The calling code will set the parent and activate it
            return arrow;
        }
        else
        {
            // If the pool is empty, create a new arrow but don't add it to the pool.
            // It will be added when it's returned.
            return GameObject.Instantiate(arrowPrefab);
        }
    }

    public void Return(GameObject arrow)
    {
        if (arrow == null) return;

        // Return the arrow to the pool's parent and deactivate it
        arrow.transform.SetParent(poolParent);
        arrow.SetActive(false);
        pool.Enqueue(arrow);
    }
}