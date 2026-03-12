// Draws the entire scene as GL wireframes via Unity's URP render hook.
// Taken from ShapeGen's rendering pipeline; stripped of rotation/screensaver
// logic and extended with DrawCube (new) and DrawBox (new).
//
// What it draws every frame
//   1. Platform  — white wireframe box
//   2. Sphere    — white wireframe sphere (from ShapeGen.DrawSphere verbatim)
//   3. Cube      — white wireframe when airborne, RED when grounded
//
// Color switching
//   Two separate GL passes with two separate materials are used instead of
//   GL.Color() because GL.Color() requires a vertex-color shader, which is
//   harder to set up. Two plain "Unlit/Color" materials (one white, one red)
//   are reliable on any Unity version.

using UnityEngine;
using UnityEngine.Rendering;

public class GameRenderer : MonoBehaviour
{
    [Header("Materials")]
    [Tooltip("Unlit/Color material set to WHITE — platform, sphere, airborne cube.")]
    public Material whiteMaterial;

    [Tooltip("Unlit/Color material set to RED — cube when it is grounded.")]
    public Material redMaterial;
    private PlayerController _player;
    void Awake()
    {
        _player = GetComponent<PlayerController>();
    }

    // Subscribe to URP's per-camera callback to draw GL lines after the main scene is rendered.
    void OnEnable()  => RenderPipelineManager.endCameraRendering += OnEndCameraRendering;
    void OnDisable() => RenderPipelineManager.endCameraRendering -= OnEndCameraRendering;

    void OnEndCameraRendering(ScriptableRenderContext ctx, Camera cam)
    {
        if (cam != Camera.main)                    return;
        if (whiteMaterial == null || redMaterial == null)
        {
            Debug.LogError("[GameRenderer] Assign whiteMaterial and redMaterial in the Inspector.");
            return;
        }

        bool grounded = _player.IsGrounded;
        bool invert   = _player.invertGroundedColor;

        // Pass 1 (white) 
        // Default  (invert=false): platform always white, cube white while airborne
        // Inverted (invert=true):  cube always white, platform white while cube is airborne
        GL.PushMatrix();
        whiteMaterial.SetPass(0);
        GL.Begin(GL.LINES);

        if (!invert)
        {
            // Platform is always white; cube is white only while airborne
            DrawBox(_player.PlatformCenter, _player.PlatformSize);
            if (!grounded) DrawCube(_player.Position, Vector3.one);
        }
        else
        {
            // Cube is always white; platform is white only while cube is airborne
            DrawCube(_player.Position, Vector3.one);
            if (!grounded) DrawBox(_player.PlatformCenter, _player.PlatformSize);
        }

        DrawSphere(_player.SpherePosition, _player.SphereRadius, sphereSegments);

        GL.End();
        GL.PopMatrix();

        // Pass 2 (red) — only fires while grounded 
        // Default  (invert=false): cube turns red
        // Inverted (invert=true):  platform turns red (added due to instructions being worded unclearly - "When the cube hits the ground change the material color to red") 
        // could be interpreted as either the cube or the platform changing color when the cube is grounded, so I implemented both options and added a toggle in the Inspector to switch between them.
        if (grounded)
        {
            GL.PushMatrix();
            redMaterial.SetPass(0);
            GL.Begin(GL.LINES);

            if (!invert)
                DrawCube(_player.Position, Vector3.one);
            else
                DrawBox(_player.PlatformCenter, _player.PlatformSize);

            GL.End();
            GL.PopMatrix();
        }
    }

    // Sphere resolution (can be tuned in Inspector) 
    [Header("Sphere Quality")]
    [Range(6, 24)]
    public int sphereSegments = 10;

    // ─────────────────────────────────────────────────────────────────────────
    // Projection 
    // Converts a 3D world point to a 2D GL coordinate via perspective divide.
    // GL.Vertex3 is called at z = 0 so these coords sit on the XY world plane,
    // visible to an orthographic camera pointing along +Z.
    Vector2 Project(Vector3 point)
    {
        float scale = PerspectiveCamera.Instance.GetPerspective(point.z);
        return new Vector2(point.x * scale, point.y * scale);
    }

    // Emits one GL line segment between two 3D world points.
    void DrawEdge(Vector3 a, Vector3 b)
    {
        Vector2 pa = Project(a);
        Vector2 pb = Project(b);
        GL.Vertex3(pa.x, pa.y, 0f);
        GL.Vertex3(pb.x, pb.y, 0f);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // DrawCube
    // Draws a wireframe cuboid: 8 corners → 12 edges.
    //   center  world-space centre of the cube
    //   size    (width, height, depth); pass Vector3.one for a unit cube
    void DrawCube(Vector3 center, Vector3 size)
    {
        float hx = size.x * 0.5f;
        float hy = size.y * 0.5f;
        float hz = size.z * 0.5f;

        // Bottom face corners (y = -hy)
        Vector3 b0 = center + new Vector3(-hx, -hy, -hz);
        Vector3 b1 = center + new Vector3( hx, -hy, -hz);
        Vector3 b2 = center + new Vector3( hx, -hy,  hz);
        Vector3 b3 = center + new Vector3(-hx, -hy,  hz);

        // Top face corners (y = +hy)
        Vector3 t0 = center + new Vector3(-hx,  hy, -hz);
        Vector3 t1 = center + new Vector3( hx,  hy, -hz);
        Vector3 t2 = center + new Vector3( hx,  hy,  hz);
        Vector3 t3 = center + new Vector3(-hx,  hy,  hz);

        // Bottom face (4 edges)
        DrawEdge(b0, b1); DrawEdge(b1, b2);
        DrawEdge(b2, b3); DrawEdge(b3, b0);

        // Top face (4 edges)
        DrawEdge(t0, t1); DrawEdge(t1, t2);
        DrawEdge(t2, t3); DrawEdge(t3, t0);

        // Vertical pillars connecting bottom to top (4 edges)
        DrawEdge(b0, t0); DrawEdge(b1, t1);
        DrawEdge(b2, t2); DrawEdge(b3, t3);
    }

    // DrawBox = DrawCube with a non-uniform size vector (platform, etc.)
    void DrawBox(Vector3 center, Vector3 size) => DrawCube(center, size);

    // ─────────────────────────────────────────────────────────────────────────
    // DrawSphere  (from ShapeGen.DrawSphere — unchanged)
    // Latitude rings (horizontal) + longitude arcs (vertical).
    void DrawSphere(Vector3 center, float radius, int segments)
    {
        // Latitude rings — horizontal circles stacked top to bottom
        for (int lat = 1; lat < segments; lat++)
        {
            float phi = Mathf.PI * lat / segments;   // 0 (top pole) → PI (bottom pole)
            float rr  = Mathf.Sin(phi) * radius;     // ring radius (0 at poles)
            float ry  = Mathf.Cos(phi) * radius;     // ring height on Y axis

            for (int lon = 0; lon < segments; lon++)
            {
                float t1 = 2f * Mathf.PI * lon       / segments;
                float t2 = 2f * Mathf.PI * (lon + 1) / segments;
                DrawEdge(
                    center + new Vector3(rr * Mathf.Cos(t1), ry, rr * Mathf.Sin(t1)),
                    center + new Vector3(rr * Mathf.Cos(t2), ry, rr * Mathf.Sin(t2)));
            }
        }

        // Longitude arcs — vertical arcs from top pole to bottom pole
        for (int lon = 0; lon < segments; lon++)
        {
            float theta = 2f * Mathf.PI * lon / segments;

            for (int lat = 0; lat < segments; lat++)
            {
                float p1 = Mathf.PI * lat       / segments;
                float p2 = Mathf.PI * (lat + 1) / segments;
                DrawEdge(
                    center + new Vector3(Mathf.Sin(p1) * Mathf.Cos(theta) * radius,
                                         Mathf.Cos(p1) * radius,
                                         Mathf.Sin(p1) * Mathf.Sin(theta) * radius),
                    center + new Vector3(Mathf.Sin(p2) * Mathf.Cos(theta) * radius,
                                         Mathf.Cos(p2) * radius,
                                         Mathf.Sin(p2) * Mathf.Sin(theta) * radius));
            }
        }
    }
}