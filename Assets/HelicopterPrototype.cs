using System;
using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.InputSystem;

public class HelicopterPrototype : MonoBehaviour
{
    [SerializeField] private Transform helicopterBody;

    [SerializeField] private float maxUpForce = 20f;

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

    private Rigidbody _body;

    private float _targetAltitude;
    private float _applyingUpForce = 0f;
    private float _upForceSpoolVelocity = 0f;

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
        }

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
