using System;
using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.InputSystem;

public class HelicopterPrototype : MonoBehaviour
{
    [SerializeField] private Transform helicopterBody;

    [SerializeField] private float maxUpForce = 25f;

    [Header("Altitude Hold")]
    [Tooltip("Metres per second the held altitude moves while space / ctrl is down.")]
    [SerializeField] private float altitudeChangeRate = 4f;
    [Tooltip("How far the held altitude may drift from the helicopter before it stops running away.")]
    [SerializeField] private float maxAltitudeError = 10f;
    [Tooltip("Altitude error to climb rate. Lower is floatier.")]
    [SerializeField] private float altitudeGain = 1.2f;
    [SerializeField] private float maxClimbRate = 5f;
    [Tooltip("Climb rate error to acceleration. Lower is floatier.")]
    [SerializeField] private float climbRateGain = 1.5f;
    [Tooltip("Seconds for the rotor to spool towards its demanded thrust.")]
    [SerializeField] private float thrustResponseTime = 0.4f;

    [Header("Bend")]
    [Tooltip("Furthest the helicopter may lean from upright, in any direction.")]
    [SerializeField] private float maxBendAngle = 45f;
    [Tooltip("Top speed the bend leans at, in degrees per second.")]
    [SerializeField] private float bendRate = 60f;
    [Tooltip("Degrees per second squared the lean speeds up and slows down. Lower is floatier.")]
    [SerializeField] private float bendAcceleration = 180f;
    [Tooltip("Degrees per second the bend unwinds while numpad 5 is down.")]
    [SerializeField] private float bendRecenterRate = 90f;

    [Header("Spin")]
    [Tooltip("Top speed the helicopter spins at while numpad 0 is down, in degrees per second.")]
    [SerializeField] private float spinRate = 90f;
    [Tooltip("Degrees per second squared the spin winds up and coasts down. Lower is floatier.")]
    [SerializeField] private float spinAcceleration = 180f;

    private Rigidbody _body;

    private float _targetAltitude;
    private float _applyingUpForce = 0f;
    private float _upForceSpoolVelocity = 0f;

    private float _bendPitch;
    private float _bendRoll;
    private float _bendPitchRate;
    private float _bendRollRate;
    private float _spinRate;

    [ContextMenu("Regen Helicopter Collisions")]
    private void GenerateHelicopterCollisions()
    {
        if (!helicopterBody) return;
        foreach (var fobj in helicopterBody.GetComponentsInChildren<Transform>().Where(t => t != helicopterBody))
        {
            fobj.gameObject.isStatic = false;
            
            if (!fobj.TryGetComponent(out MeshFilter mf)) continue;
            if (!fobj.TryGetComponent(out Collider mcollider))
            {
                mcollider = fobj.gameObject.AddComponent<BoxCollider>();
            }
            if (mcollider is BoxCollider boxCollider && mf.sharedMesh != null)
            {
                boxCollider.center = mf.sharedMesh.bounds.center;
                boxCollider.size = mf.sharedMesh.bounds.size;
            }
        }
        EditorUtility.SetDirty(gameObject);
        PrefabUtility.RecordPrefabInstancePropertyModifications(gameObject);
    }

    private void Awake()
    {
        _body = GetComponent<Rigidbody>();
        _targetAltitude = transform.position.y;
    }

    // Numpad 8/2 pitch, 4/6 roll, 5 unwinds back to upright. The bend is sticky:
    // it holds wherever you let go, rather than springing back on its own.
    private void UpdateBend(Keyboard keyboard)
    {
        var deltaTime = Time.fixedDeltaTime;

        // Positive pitch drops the nose, positive roll drops the left side.
        var targetPitchRate = 0f;
        var targetRollRate = 0f;
        if (keyboard.numpad8Key.isPressed) targetPitchRate += bendRate;
        if (keyboard.numpad2Key.isPressed) targetPitchRate -= bendRate;
        if (keyboard.numpad4Key.isPressed) targetRollRate += bendRate;
        if (keyboard.numpad6Key.isPressed) targetRollRate -= bendRate;

        // Ease up to the lean speed and coast back down on release, rather than
        // snapping to full rate the instant a key goes down.
        var accelerationStep = bendAcceleration * deltaTime;
        _bendPitchRate = Mathf.MoveTowards(_bendPitchRate, targetPitchRate, accelerationStep);
        _bendRollRate = Mathf.MoveTowards(_bendRollRate, targetRollRate, accelerationStep);

        _bendPitch += _bendPitchRate * deltaTime;
        _bendRoll += _bendRollRate * deltaTime;

        if (keyboard.numpad5Key.isPressed)
        {
            var recenterStep = bendRecenterRate * deltaTime;
            _bendPitch = Mathf.MoveTowards(_bendPitch, 0f, recenterStep);
            _bendRoll = Mathf.MoveTowards(_bendRoll, 0f, recenterStep);
            // Bleed the lean momentum too, or it keeps pushing back out against the recenter.
            _bendPitchRate = Mathf.MoveTowards(_bendPitchRate, 0f, accelerationStep);
            _bendRollRate = Mathf.MoveTowards(_bendRollRate, 0f, accelerationStep);
        }

        // Clamp the pair together so a diagonal lean is capped at the same angle
        // as a straight one, instead of compounding to ~60 degrees off vertical.
        var bend = new Vector2(_bendPitch, _bendRoll);
        if (bend.sqrMagnitude > maxBendAngle * maxBendAngle)
        {
            var outward = bend.normalized;
            bend = outward * maxBendAngle;
            // Shed only the momentum pushing into the cap, so a lean held at the limit
            // can still slide sideways and releases without a stored-up snap.
            var rate = new Vector2(_bendPitchRate, _bendRollRate);
            rate -= outward * Mathf.Max(Vector2.Dot(rate, outward), 0f);
            _bendPitchRate = rate.x;
            _bendRollRate = rate.y;
        }
        _bendPitch = bend.x;
        _bendRoll = bend.y;
    }

    // Numpad 0 spins the helicopter clockwise seen from above, winding up and
    // coasting down the same way the bend does.
    private void UpdateSpin(Keyboard keyboard)
    {
        var targetSpinRate = keyboard.numpad0Key.isPressed ? spinRate : 0f;
        _spinRate = Mathf.MoveTowards(_spinRate, targetSpinRate, spinAcceleration * Time.fixedDeltaTime);
    }

    private void ApplyRotation()
    {
        // Rebuild the rotation from the current heading each step so the bend stays
        // relative to whichever way the helicopter is facing.
        var flatForward = Vector3.ProjectOnPlane(_body.rotation * Vector3.forward, Vector3.up);
        if (flatForward.sqrMagnitude < 0.0001f)
        {
            // Nose is pointing straight up or down; fall back to the body's up for a heading.
            flatForward = Vector3.ProjectOnPlane(_body.rotation * Vector3.up, Vector3.up);
        }
        if (flatForward.sqrMagnitude < 0.0001f) return;

        // Positive yaw turns the nose right, i.e. clockwise from above.
        var heading = Quaternion.LookRotation(flatForward, Vector3.up)
                      * Quaternion.Euler(0f, _spinRate * Time.fixedDeltaTime, 0f);
        _body.MoveRotation(heading * Quaternion.Euler(_bendPitch, 0f, _bendRoll));
    }

    private void FixedUpdate()
    {
        var keyboard = Keyboard.current;
        if (keyboard != null)
        {
            if (keyboard.spaceKey.isPressed)
            {
                _targetAltitude += altitudeChangeRate * Time.fixedDeltaTime;
            }
            if (keyboard.leftCtrlKey.isPressed || keyboard.rightCtrlKey.isPressed)
            {
                _targetAltitude -= altitudeChangeRate * Time.fixedDeltaTime;
            }
            UpdateBend(keyboard);
            UpdateSpin(keyboard);
        }

        ApplyRotation();

        var altitude = _body.position.y;
        // Stop the held altitude running away from a helicopter that can't follow it,
        // e.g. holding ctrl while sat on the ground.
        _targetAltitude = Mathf.Clamp(_targetAltitude, altitude - maxAltitudeError, altitude + maxAltitudeError);

        // Outer loop: how fast we'd like to be climbing to close the altitude error.
        var desiredClimbRate = Mathf.Clamp((_targetAltitude - altitude) * altitudeGain, -maxClimbRate, maxClimbRate);
        // Inner loop: the acceleration that gets us there, plus gravity so zero error hovers.
        var demandedForce = (desiredClimbRate - _body.linearVelocity.y) * climbRateGain - Physics.gravity.y;

        // Spool towards the demand rather than snapping to it, which is what makes it feel floaty.
        _applyingUpForce = Mathf.SmoothDamp(_applyingUpForce, demandedForce, ref _upForceSpoolVelocity, thrustResponseTime);
        _applyingUpForce = Mathf.Clamp(_applyingUpForce, 0f, maxUpForce);

        // Thrust follows the body's local up, so any tilt trades lift for translation.
        // Scale it back up to keep the vertical component honest, guarded against extreme tilt.
        var uprightness = Mathf.Max(Vector3.Dot(transform.up, Vector3.up), 0.25f);
        _body.AddRelativeForce(Vector3.up * (_applyingUpForce / uprightness), ForceMode.Acceleration);
    }
}
