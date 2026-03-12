// Owns all player physics, input, collision, and game-state transitions.
//
// Physics model
//   • Gravity pulls the cube down each frame while airborne.
//   • Full AABB collision is resolved on all 6 faces using a minimum-overlap
//     (MTV) push — the axis with the smallest penetration depth is chosen and
//     the cube is pushed out along that axis. X and Y axes only; Z is the
//     scene depth and is never resolved (everything shares z = 5).
//   • Axes are resolved separately: horizontal first, then vertical.
//     This prevents corner-catching where both axes fire at once.
//   • Walk-off: IsGrounded is cleared the moment the cube's X range no
//     longer overlaps the platform, causing gravity to resume.
//   • Air jump: the cube gets one free jump while airborne to recover from
//     a fall. It is consumed on use and restored the moment the cube lands.
//     If the cube never lands and drops below deathY it still dies.
//
// Death / restart conditions
//   1. Sphere touch  — cube centre-to-sphere-centre distance < (sphereRadius + cubeHalf)
//   2. Fall death    — cube Y drops below deathY (player jumped or walked off the platform)
//
// Scene layout
//   PlatformCenter / PlatformSize / SpherePosition / SphereRadius are public
//   fields so GameRenderer can read them and draw objects at the exact same
//   positions used for collision — single source of truth.

using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.SceneManagement;

public class PlayerController : MonoBehaviour
{
    // GameRenderer reads these fields to draw objects at the same positions.

    [Header("Platform (must match what GameRenderer draws)")]
    [Tooltip("World-space centre of the long platform.")]
    public Vector3 PlatformCenter = new Vector3(0f, -4f, 5f);

    [Tooltip("Width, height, depth of the platform box.")]
    public Vector3 PlatformSize   = new Vector3(20f, 0.8f, 2f);

    [Header("Sphere (goal / instant-kill)")]
    [Tooltip("World-space centre of the sphere at rest. Place to the right on the platform.")]
    public Vector3 sphereOrigin     = new Vector3(7f, -2.9f, 5f);
    [Tooltip("Base radius of the sphere.")]
    public float   sphereBaseRadius = 0.6f;

    [Header("Sphere Animation")]
    [Tooltip("How many units the sphere bobs up and down.")]
    public float bobAmplitude   = 0.4f;
    [Tooltip("Full bobs per second.")]
    public float bobSpeed       = 0.5f;

    // Animated values — read by GameRenderer and collision checks every frame.
    public Vector3 SpherePosition { get; private set; }
    public float   SphereRadius   { get; private set; }

    // Player settings
    [Header("Spawn")]
    [Tooltip("Cube starts here — above the platform so it drops down on play.")]
    public Vector3 spawnPosition = new Vector3(-3f, 3f, 5f);

    [Header("Horizontal Movement")]
    public float moveSpeed = 5f;

    [Header("Jump")]
    public float jumpForce = 12f;

    [Header("Gravity")]
    [Tooltip("Downward acceleration applied while airborne (units/s²).")]
    public float gravity = 30f;

    [Header("Death")]
    [Tooltip("If the cube falls below this Y value, the scene restarts (fall-off death).")]
    public float deathY = -10f;

    [Header("Debug")]
    [Tooltip("Default (false): the CUBE turns red when it lands on the platform.\n" +
             "Inverted (true): the PLATFORM turns red when the cube lands on it.")]
    public bool invertGroundedColor = false;

    // Public read-only state (used by GameRenderer) 
    public Vector3 Position   { get; private set; }

    // True when the cube is resting on the platform — GameRenderer
    // uses this to switch from white to red.
    public bool    IsGrounded { get; private set; }

    // Private physics 
    // Half-extent of the player cube (the cube is drawn at 1×1×1).
    private const float CubeHalf = 0.5f;

    private float _velocityY;

    // Single air-jump token — granted on landing, consumed mid-air.
    // Lets the player recover from a fall once before dying.
    private bool _hasAirJump = false;

    // Guard flag — prevents multiple Restart() calls in the same frame
    // (e.g. sphere + fall-death coinciding).
    private bool _restarting = false;

    void Start()
    {
        Position       = spawnPosition;
        SpherePosition = sphereOrigin;      // initial value before first AnimateSphere()
        SphereRadius   = sphereBaseRadius;
    }

    void Update()
    {
        if (_restarting) return;

        AnimateSphere();
        HandleJumpInput();
        ApplyGravity();
        MoveHorizontal();
        MoveVertical();
        ValidateGrounded();   // clear IsGrounded if cube walked off the edge
        CheckSphereHit();
        CheckFallDeath();
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Jump input
    //
    // Ground jump  — standard; clears IsGrounded immediately.
    // Air jump     — one free jump while airborne, consumed on use.
    //                Token is restored the moment the cube lands (MoveVertical).
    //                This lets the player recover from a fall without giving
    //                unlimited flight — if they still miss the platform, deathY
    //                triggers as normal.
    void HandleJumpInput()
    {
        var kb = Keyboard.current;
        if (kb == null) return;

        bool jumpPressed = kb.spaceKey.wasPressedThisFrame
                        || kb.wKey.wasPressedThisFrame
                        || kb.upArrowKey.wasPressedThisFrame;

        if (jumpPressed && IsGrounded)
        {
            _velocityY = jumpForce;
            IsGrounded = false;     // leave the ground immediately
        }
        else if (jumpPressed && !IsGrounded && _hasAirJump)
        {
            // Air jump — only one per airborne spell
            _velocityY  = jumpForce;
            _hasAirJump = false;    // token consumed; not restored until next landing
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Gravity — accumulates downward velocity while the cube is airborne
    void ApplyGravity()
    {
        if (IsGrounded)
        {
            _velocityY = 0f;    // kill any residual velocity while standing
            return;
        }
        _velocityY -= gravity * Time.deltaTime;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Horizontal movement  (A/← = left, D/→ = right)
    //
    // Move X first, then run AABB. If a side face is hit (normal.x != 0) the
    // cube is pushed back out along X — it cannot walk through the platform
    // from the side. Horizontal velocity is per-frame input so no separate
    // velocity zeroing is needed here; the push just prevents penetration.
    void MoveHorizontal()
    {
        var kb = Keyboard.current;
        if (kb == null) return;

        float input = 0f;
        if (kb.aKey.isPressed || kb.leftArrowKey.isPressed)  input -= 1f;
        if (kb.dKey.isPressed || kb.rightArrowKey.isPressed) input += 1f;

        float newX = Position.x + input * moveSpeed * Time.deltaTime;
        Position = new Vector3(newX, Position.y, Position.z);

        // Resolve side collision — push out along X if penetrating
        if (GetCollisionCorrection(out Vector3 correction, out Vector3 normal))
        {
            if (Mathf.Abs(normal.x) > 0.5f)
                Position = new Vector3(Position.x + correction.x, Position.y, Position.z);
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Vertical movement + full AABB collision response
    //
    // Move Y first, then run AABB collision:
    //   normal.y > 0  (top face hit)    → cube landed on the platform.
    //                                      Snap flush, zero velocity, set grounded,
    //                                      restore the air-jump token.
    //   normal.y < 0  (bottom face hit) → cube hit the underside of the platform
    //                                      (jumped from below); zero upward velocity
    //                                      so it doesn't stick to the ceiling.
    void MoveVertical()
    {
        float   dy     = _velocityY * Time.deltaTime;
        Vector3 newPos = new Vector3(Position.x, Position.y + dy, Position.z);
        Position = newPos;

        if (GetCollisionCorrection(out Vector3 correction, out Vector3 normal))
        {
            if (normal.y > 0.5f)
            {
                // Landed on top face — push cube up flush with the surface
                Position    = new Vector3(Position.x, Position.y + correction.y, Position.z);
                _velocityY  = 0f;
                IsGrounded  = true;
                _hasAirJump = true;     // restore air-jump token on landing
            }
            else if (normal.y < -0.5f)
            {
                // Hit the underside — push cube down and kill upward momentum
                Position   = new Vector3(Position.x, Position.y + correction.y, Position.z);
                _velocityY = 0f;
            }
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // GetCollisionCorrection — full AABB MTV resolver (X and Y axes only)
    //
    // Calculates how far the cube overlaps the platform on X and Y.
    // Resolves along the axis with the smaller penetration depth (minimum
    // translation vector) — this matches what the eye expects: hitting the
    // top of the platform resolves upward, hitting the side resolves sideways.
    //
    // Z is the scene depth; everything is at z = 5 so we only confirm Z
    // overlap as a gate check and never push along it.
    //
    // Returns true  + correction vector + face normal if overlapping.
    // Returns false + Vector3.zero      if no overlap.
    bool GetCollisionCorrection(out Vector3 correction, out Vector3 normal)
    {
        correction = Vector3.zero;
        normal     = Vector3.zero;

        // Z gate check — shared depth layer 
        // If the cube and platform don't overlap in Z, skip entirely.
        float pHalfZ = CubeHalf;
        float tHalfZ = PlatformSize.z * 0.5f;
        if (Mathf.Abs(Position.z - PlatformCenter.z) >= pHalfZ + tHalfZ) return false;

        // X overlap 
        float dx       = Position.x - PlatformCenter.x;
        float overlapX = (CubeHalf + PlatformSize.x * 0.5f) - Mathf.Abs(dx);
        if (overlapX <= 0f) return false;   // separated on X — no collision

        // Y overlap 
        float dy       = Position.y - PlatformCenter.y;
        float overlapY = (CubeHalf + PlatformSize.y * 0.5f) - Mathf.Abs(dy);
        if (overlapY <= 0f) return false;   // separated on Y — no collision

        // Resolve along the minimum-overlap axis 
        // Smaller overlap = the face the cube most recently crossed — push out there.
        if (overlapX < overlapY)
        {
            // Side face — push left or right
            float signX = Mathf.Sign(dx);
            correction  = new Vector3(overlapX * signX, 0f, 0f);
            normal      = new Vector3(signX, 0f, 0f);
        }
        else
        {
            // Top or bottom face — push up or down
            float signY = Mathf.Sign(dy);
            correction  = new Vector3(0f, overlapY * signY, 0f);
            normal      = new Vector3(0f, signY, 0f);
        }
        return true;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Walk-off detection
    // Each frame while grounded, verify the cube still overlaps the platform
    // in X. If it doesn't, the player has walked off the edge — clear the
    // grounded flag so gravity resumes.
    void ValidateGrounded()
    {
        if (!IsGrounded) return;

        float platMinX = PlatformCenter.x - PlatformSize.x * 0.5f;
        float platMaxX = PlatformCenter.x + PlatformSize.x * 0.5f;

        bool stillOverPlatform =
            (Position.x + CubeHalf > platMinX) &&
            (Position.x - CubeHalf < platMaxX);

        if (!stillOverPlatform)
            IsGrounded = false;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Sphere animation — bob (Y offset) only.
    // SpherePosition is also used by CheckSphereHit, so collision always
    // matches exactly what the renderer draws.
    void AnimateSphere()
    {
        float bobOffset = Mathf.Sin(Time.time * bobSpeed * Mathf.PI * 2f) * bobAmplitude;
        SpherePosition  = new Vector3(sphereOrigin.x,
                                      sphereOrigin.y + bobOffset,
                                      sphereOrigin.z);
        SphereRadius    = sphereBaseRadius;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Sphere hit detection — centre-to-centre distance check.
    // Using cube-centre-to-sphere-centre distance vs (sphereRadius + CubeHalf)
    // gives a slightly generous hit box which feels natural for a pickup.
    void CheckSphereHit()
    {
        float dist = Vector3.Distance(Position, SpherePosition);
        if (dist < SphereRadius + CubeHalf)
        {
            Debug.Log("[PlayerController] Touched sphere — restarting.");
            Restart();
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Fall-death — triggered if the cube drops below deathY.
    // This covers: jumping off the platform intentionally, or walking off
    // either end of the platform and falling.
    void CheckFallDeath()
    {
        if (Position.y < deathY)
        {
            Debug.Log("[PlayerController] Fell to death — restarting.");
            Restart();
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Restart — reloads the active scene (resets all state).
    void Restart()
    {
        if (_restarting) return;     // prevent double-call in the same frame
        _restarting = true;
        SceneManager.LoadScene(SceneManager.GetActiveScene().buildIndex);
    }
}