// Singleton. Provides the perspective scale factor used by GameRenderer
// to project 3D world points into 2D GL screen coordinates.
//
// focalLength / (focalLength + zPos)
//   → 1.0  when zPos = 0             (no shrink)
//   → 0.5  when zPos = focalLength   (half-size at twice the focal depth)
//   → ~0   as zPos approaches ∞
//
using UnityEngine;

public class PerspectiveCamera : MonoBehaviour
{
    public static PerspectiveCamera Instance;

    public float focalLength = 5f;

    void Awake()
    {
        // No DontDestroyOnLoad — the scene reloads fully on restart,
        // so a fresh instance is created each time. Keeping the old one
        // alive would duplicate every script on this GameObject.
        Instance = this;
    }

    public float GetPerspective(float zPos)
    {
        // Guard against divide-by-zero or negative depth
        return focalLength / Mathf.Max(focalLength + zPos, 0.001f);
    }
}