using System;
using UnityEngine;
using UnityEngine.InputSystem;

namespace PushUp.Gameplay
{
    /// <summary>Project-owned gameplay action map shared by offline and network player controllers.</summary>
    public sealed class PlayerInputReader : MonoBehaviour
    {
        private static bool _gameplayEnabled;

        private InputActionMap _map;
        private InputAction _move;
        private InputAction _look;
        private InputAction _jump;
        private InputAction _sprint;
        private InputAction _crouch;
        private InputAction _grab;
        private InputAction _punch;
        private InputAction _anchor;
        private InputAction _pause;

        private bool _jumpQueued;
        private bool _crouchQueued;
        private bool _punchQueued;
        private bool _anchorQueued;
        private bool _pauseQueued;
        private bool _localControlEnabled;

        /// <summary>
        /// The menu owns this gate. Keeping it here lets offline and network players
        /// follow the same UI/input rule without depending on a particular menu class.
        /// </summary>
        public static bool GameplayEnabled => _gameplayEnabled;

        public Vector2 Move => _gameplayEnabled ? _move?.ReadValue<Vector2>() ?? Vector2.zero : Vector2.zero;
        public Vector2 Look => _gameplayEnabled ? _look?.ReadValue<Vector2>() ?? Vector2.zero : Vector2.zero;
        public bool LookUsesRate => _look?.activeControl?.device is Gamepad;
        public bool GrabHeld => _gameplayEnabled && (_grab?.IsPressed() ?? false);
        public bool JumpHeld => _gameplayEnabled && (_jump?.IsPressed() ?? false);
        public bool SprintHeld => _gameplayEnabled && (_sprint?.IsPressed() ?? false);
        public bool CrouchHeld => _gameplayEnabled && (_crouch?.IsPressed() ?? false);

        public static void SetGameplayEnabled(bool enabled) => _gameplayEnabled = enabled;

        private void Awake()
        {
            _map = new InputActionMap("Player");

            _move = _map.AddAction("Move", InputActionType.Value);
            _move.expectedControlType = "Vector2";
            _move.AddCompositeBinding("2DVector")
                .With("Up", "<Keyboard>/w").With("Down", "<Keyboard>/s")
                .With("Left", "<Keyboard>/a").With("Right", "<Keyboard>/d");
            _move.AddBinding("<Gamepad>/leftStick");

            _look = _map.AddAction("Look", InputActionType.Value);
            _look.expectedControlType = "Vector2";
            _look.AddBinding("<Mouse>/delta");
            _look.AddBinding("<Gamepad>/rightStick");

            _jump = AddButton("Jump", "<Keyboard>/space", "<Gamepad>/buttonSouth");
            _sprint = AddButton("Sprint", "<Keyboard>/leftShift", "<Gamepad>/leftStickPress");
            _crouch = AddButton("Crouch", "<Keyboard>/leftCtrl", "<Gamepad>/buttonEast");
            // Keep the keyboard alternative, but make the mouse pair feel immediate:
            // left punches and right holds a grab.
            _grab = AddButton("Grab", "<Mouse>/rightButton", "<Keyboard>/e");
            _grab.AddBinding("<Gamepad>/buttonWest");
            _punch = AddButton("Punch", "<Mouse>/leftButton", "<Gamepad>/rightTrigger");
            _anchor = AddButton("Anchor", "<Keyboard>/q", "<Gamepad>/leftShoulder");
            _pause = AddButton("Pause", "<Keyboard>/escape", "<Gamepad>/start");

            _jump.performed += QueueJump;
            _crouch.performed += QueueCrouch;
            _punch.performed += QueuePunch;
            _anchor.performed += QueueAnchor;
            _pause.performed += QueuePause;
        }

        private InputAction AddButton(string name, string firstBinding, string secondBinding)
        {
            InputAction action = _map.AddAction(name, InputActionType.Button);
            action.AddBinding(firstBinding);
            action.AddBinding(secondBinding);
            return action;
        }

        public bool LocalControlEnabled => _localControlEnabled;

        /// <summary>
        /// Network prefabs exist once per remote player, but only the locally controlled
        /// instance should install device monitors and receive action callbacks.
        /// </summary>
        public void SetLocalControlEnabled(bool enabled)
        {
            _localControlEnabled = enabled;
            if (!isActiveAndEnabled || _map == null)
                return;
            if (enabled)
                _map.Enable();
            else
            {
                _map.Disable();
                ClearQueuedGameplayActions();
            }
        }

        private void OnEnable()
        {
            if (_localControlEnabled)
                _map?.Enable();
        }
        private void OnDisable() => _map?.Disable();

        private void OnDestroy()
        {
            if (_jump != null) _jump.performed -= QueueJump;
            if (_crouch != null) _crouch.performed -= QueueCrouch;
            if (_punch != null) _punch.performed -= QueuePunch;
            if (_anchor != null) _anchor.performed -= QueueAnchor;
            if (_pause != null) _pause.performed -= QueuePause;
            _map?.Dispose();
        }

        public bool ConsumeJump() => ConsumeGameplay(ref _jumpQueued);
        public bool ConsumeCrouchPress() => ConsumeGameplay(ref _crouchQueued);
        public bool ConsumePunch() => ConsumeGameplay(ref _punchQueued);
        public bool ConsumeAnchor() => ConsumeGameplay(ref _anchorQueued);
        public bool ConsumePause() => Consume(ref _pauseQueued);

        private static bool ConsumeGameplay(ref bool queued)
        {
            if (!_gameplayEnabled)
            {
                queued = false;
                return false;
            }

            return Consume(ref queued);
        }

        private static bool Consume(ref bool queued)
        {
            bool value = queued;
            queued = false;
            return value;
        }

        private void ClearQueuedGameplayActions()
        {
            _jumpQueued = false;
            _crouchQueued = false;
            _punchQueued = false;
            _anchorQueued = false;
            _pauseQueued = false;
        }

        private void QueueJump(InputAction.CallbackContext _) => _jumpQueued = _gameplayEnabled;
        private void QueueCrouch(InputAction.CallbackContext _) => _crouchQueued = _gameplayEnabled;
        private void QueuePunch(InputAction.CallbackContext _) => _punchQueued = _gameplayEnabled;
        private void QueueAnchor(InputAction.CallbackContext _) => _anchorQueued = _gameplayEnabled;
        private void QueuePause(InputAction.CallbackContext _) => _pauseQueued = true;
    }
}
