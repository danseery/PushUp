using UnityEngine;

namespace PushUp.Gameplay
{
    /// <summary>
    /// Client-only, bounded presentation prediction for the host-authored boulder. This component never
    /// writes to the Rigidbody or network root.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class BoulderVisualPredictor : MonoBehaviour
    {
        public const float MaximumPositionOffset = 0.35f;
        public const float MaximumRotationOffsetDegrees = 12f;
        public const float CorrectionHalfLifeSeconds = 0.10f;
        private const float IntentLifetimeSeconds = 0.075f;

        private Rigidbody _body;
        private Vector3 _continuousForce;
        private Vector3 _continuousTorque;
        private Vector3 _velocityOffset;
        private Vector3 _angularVelocityOffsetDegrees;
        private Vector3 _positionOffset;
        private Vector3 _rotationOffsetDegrees;
        private float _intentExpiresAt;

        public Vector3 PositionOffset => _positionOffset;
        public float RotationOffsetDegrees => _rotationOffsetDegrees.magnitude;
        public float PredictionMagnitude => _positionOffset.magnitude;

        private void Awake() => _body = GetComponent<Rigidbody>();

        public void SetContinuousForce(Vector3 force, Vector3 torque)
        {
            if (!IsFinite(force) || !IsFinite(torque))
                return;
            _continuousForce = force;
            _continuousTorque = torque;
            _intentExpiresAt = Time.unscaledTime + IntentLifetimeSeconds;
        }

        public void AddImpulse(Vector3 impulse, Vector3 worldPoint)
        {
            if (!IsFinite(impulse) || !IsFinite(worldPoint))
                return;
            _body ??= GetComponent<Rigidbody>();
            float mass = Mathf.Max(1f, _body != null ? _body.mass : BoulderController.DefaultMass);
            _velocityOffset += impulse / mass;
            Vector3 center = _body != null ? _body.worldCenterOfMass : transform.position;
            Vector3 angularImpulse = Vector3.Cross(worldPoint - center, impulse);
            float radius = Mathf.Max(0.5f, GetComponent<Collider>()?.bounds.extents.magnitude ?? 1f);
            float approximateInertia = Mathf.Max(1f, 0.4f * mass * radius * radius);
            _angularVelocityOffsetDegrees += angularImpulse / approximateInertia * Mathf.Rad2Deg;
        }

        public void CancelTransientPrediction()
        {
            _continuousForce = Vector3.zero;
            _continuousTorque = Vector3.zero;
            _intentExpiresAt = 0f;
        }

        public void ResetPrediction()
        {
            CancelTransientPrediction();
            _velocityOffset = Vector3.zero;
            _angularVelocityOffsetDegrees = Vector3.zero;
            _positionOffset = Vector3.zero;
            _rotationOffsetDegrees = Vector3.zero;
        }

        public void Simulate(float deltaTime)
        {
            deltaTime = Mathf.Clamp(deltaTime, 0f, 0.05f);
            if (deltaTime <= 0f)
                return;
            _body ??= GetComponent<Rigidbody>();
            float mass = Mathf.Max(1f, _body != null ? _body.mass : BoulderController.DefaultMass);
            bool intentActive = Time.unscaledTime <= _intentExpiresAt;
            if (intentActive)
            {
                _velocityOffset += Vector3.ClampMagnitude(_continuousForce / mass, 12f) * deltaTime;
                float radius = Mathf.Max(0.5f, GetComponent<Collider>()?.bounds.extents.magnitude ?? 1f);
                float approximateInertia = Mathf.Max(1f, 0.4f * mass * radius * radius);
                _angularVelocityOffsetDegrees += Vector3.ClampMagnitude(
                    _continuousTorque / approximateInertia * Mathf.Rad2Deg, 720f) * deltaTime;
            }

            _positionOffset += _velocityOffset * deltaTime;
            _rotationOffsetDegrees += _angularVelocityOffsetDegrees * deltaTime;
            _positionOffset = Vector3.ClampMagnitude(_positionOffset, MaximumPositionOffset);
            _rotationOffsetDegrees = Vector3.ClampMagnitude(_rotationOffsetDegrees,
                MaximumRotationOffsetDegrees);

            float decay = Mathf.Exp(-Mathf.Log(2f) * deltaTime / CorrectionHalfLifeSeconds);
            _velocityOffset *= decay;
            _angularVelocityOffsetDegrees *= decay;
            _positionOffset *= decay;
            _rotationOffsetDegrees *= decay;
            if (!intentActive)
            {
                _continuousForce = Vector3.zero;
                _continuousTorque = Vector3.zero;
            }
        }

        public Quaternion RotationOffset
        {
            get
            {
                float angle = _rotationOffsetDegrees.magnitude;
                return angle > 0.0001f
                    ? Quaternion.AngleAxis(angle, _rotationOffsetDegrees / angle)
                    : Quaternion.identity;
            }
        }

        private static bool IsFinite(Vector3 value) =>
            float.IsFinite(value.x) && float.IsFinite(value.y) && float.IsFinite(value.z);
    }
}
