// PlayerMovement.cs

using UnityEngine;
using UnityEngine.InputSystem;
using FishNet.Object;
using System.Collections.Generic;

public class PlayerMovement : NetworkBehaviour
{
    [Header("Movement Settings")]
    [SerializeField] private float _walkSpeed = 4f;
    [SerializeField] private float _sprintSpeed = 8f;
    [SerializeField] private float _jumpHeight = 1.5f;
    [SerializeField] private float _gravity = -19.62f;

    // How fast the body rotates to face its movement direction while in
    // PlayerCamera.CameraMode.ThirdPerson (minigames) — see HandleMovement.
    // 720 = a full turn in half a second; tune in Inspector once this is
    // actually visible in Editor.
    [SerializeField] private float _autoTurnSpeed = 720f;

    [Header("Ground Check")]
    [SerializeField] private LayerMask _groundLayer;
    [SerializeField] private float _groundCheckDistance = 0.1f;

    [Header("Crouch Settings")]
    [SerializeField] private float _crouchSpeed = 2f;
    [SerializeField] private float _crouchHeight = 0.9f;
    [SerializeField] private float _standHeight = 1.8f;
    [SerializeField] private float _crouchTransitionSpeed = 10f;

    // Camera eye height while crouched — tune in Inspector to match how low
    // the crouch pose actually puts the model's head. The standing target
    // (_standCameraHeight) is NOT a field: it's captured at spawn from
    // CameraRoot's own authored position (see Initialize), since that anchor
    // is already placed correctly and shouldn't be re-guessed as a number
    // here. Deriving camera height from _controller.height (the old
    // approach) was wrong — collision capsule size and eye height are
    // unrelated, and that coupling broke the moment Height was tuned
    // separately for wall-clearance/collision purposes.
    [SerializeField] private float _crouchCameraHeight = 0.7f;

    [Header("Animation")]
    private Animator _animator;

    [Header("Key Bindings")]
    [SerializeField] private Key _crouchKey = Key.C;
    [SerializeField] private Key _shoveKey = Key.F;

    [Header("Sprint / Stamina")]
    [SerializeField] private float _maxStamina = 3f;        // seconds of sprint available
    [SerializeField] private float _staminaDrainRate = 1f;  // per second while sprinting
    [SerializeField] private float _staminaRegenRate = 0.5f; // per second while not sprinting
    [SerializeField] private float _staminaRegenDelay = 1.5f; // cooldown before regen starts

    private float _currentStamina;
    private float _regenDelayTimer;
    private bool _staminaLimited = false; // off = unlimited sprint (lobby default)

    public float CurrentStamina => _currentStamina;
    public float MaxStamina => _maxStamina;

    private bool _isCrouching;
    private float _targetHeight;
    private bool _isJumping;

    // Camera eye-height state — fully independent of _controller.height/
    // _targetHeight (see _crouchCameraHeight comment above for why).
    private float _standCameraHeight;
    private float _targetCameraHeight;

    private PlayerObject _player;
    private CharacterController _controller;
    private Vector3 _velocity;
    private bool _isGrounded;

    // Lock-stack: each caller locks/unlocks under its own source key so
    // unrelated systems (pause menu, sitting, stun, etc.) can't clobber
    // each other's lock state. Movement is locked if this set is non-empty.
    private readonly HashSet<string> _movementLockSources = new HashSet<string>();

    private bool _isSprinting;
    private Sittable _currentSeat;

    private bool _isMoving;
    public bool IsCrouching => _isCrouching;
    public bool IsJumping => _isJumping;

    private float _jumpCooldown = 0f;
    private const float JumpCooldownDuration = 0.2f;
    private float _standUpJumpBlockTimer = 0f;
    private float _shoveCooldown = 0f;

    private float _moveX;
    private float _moveY;

    public bool IsMovementLocked => _movementLockSources.Count > 0;
    public bool IsSprinting => _isSprinting;

    public void SetCurrentSeat(Sittable seat) => _currentSeat = seat;

    public void Initialize(PlayerObject player)
    {
        _player = player;
        _controller = player.GetComponent<CharacterController>();
        _targetHeight = _standHeight;
        _controller.height = _targetHeight;

        // Force center.y to height/2 at spawn instead of trusting whatever raw value
        // is authored on the CharacterController in the Inspector — HandleCrouch only
        // keeps center.y in sync with height while the two differ, so if a future
        // edit sets Height and Center.y to values that don't already agree, the
        // capsule would otherwise float or sink relative to the root with no
        // transition ever running to correct it, breaking ground detection.
        _controller.center = new Vector3(_controller.center.x, _targetHeight / 2f, _controller.center.z);

        // Capture the CameraRoot's own authored position as the standing eye
        // height, rather than hardcoding a number — whatever the anchor is
        // placed at in the prefab/scene is the source of truth for "standing."
        if (_player.CameraRoot != null)
            _standCameraHeight = _player.CameraRoot.localPosition.y;
        _targetCameraHeight = _standCameraHeight;

        _isGrounded = true;
        _animator?.SetBool("IsGrounded", true);
        _animator?.SetBool("IsJumping", false);
        _currentStamina = _maxStamina;

        // Find animator immediately on the CharacterModel
        _animator = player.CharacterModel.GetComponent<Animator>();
        if (_animator == null)
            _animator = player.CharacterModel.GetComponentInChildren<Animator>();

        //Debug.Log($"[PlayerMovement] Animator found: {_animator != null}, GameObject: {_animator?.gameObject.name}");
    }

    private void Update()
    {
        //Debug.Log($"Movement Update — IsOwner: {IsOwner}, Locked: {IsMovementLocked}");
        if (!IsOwner) return;

        if (IsMovementLocked)
        {
            HandleSeatExit();

            // Force the locomotion blend back to idle while locked. This branch
            // used to return before UpdateAnimator() ran again, so MoveX/MoveY
            // stayed frozen at whatever nonzero value they had the instant the
            // lock engaged — the walk/run animation kept cycling in place for
            // the whole lock duration (e.g. a Thief's Market punch stun) even
            // though the character's actual position was correctly frozen.
            _moveX = 0f;
            _moveY = 0f;
            UpdateAnimator();
            return;
        }

        HandleGroundCheck();
        HandleMovement();
        HandleJump();
        HandleCrouch();
        HandleShove();
        ApplyGravity();
        UpdateAnimator();
    }

    private void HandleSeatExit()
    {
        var keyboard = Keyboard.current;
        if (keyboard == null) return;

        //Debug.Log($"HandleSeatExit — CurrentSeat: {_currentSeat?.name ?? "null"}");

        if (_currentSeat != null && keyboard.spaceKey.wasPressedThisFrame)
        {
            // Debug.Log("Standing from seat");
            _currentSeat.ForceStand(_player);
            _currentSeat = null;

            // Prevent the same/next space press from immediately registering as a jump
            _standUpJumpBlockTimer = 0.3f;
        }
    }

    private void UpdateAnimator()
    {
        //Debug.Log($"Speed: {_animator.GetFloat("Speed")}, IsGrounded: {_isGrounded}");

        if (_animator == null) return;


        // Debug.Log($"Speed: {speed}, IsSprinting: {_isSprinting}, IsMoving: {_isMoving}");

        _animator.SetFloat("MoveX", _moveX);
        _animator.SetFloat("MoveY", _moveY);
        _animator.SetBool("IsCrouching", _isCrouching);
        _animator.SetBool("IsGrounded", _isGrounded);
        _animator.SetBool("IsJumping", _isJumping);
    }

    private void HandleGroundCheck()
    {
        bool wasGrounded = _isGrounded;

        if (_jumpCooldown > 0f)
        {
            _isGrounded = false;
        }
        else
        {
            _isGrounded = Physics.Raycast(
                transform.position + Vector3.up * 0.1f,
                Vector3.down,
                _groundCheckDistance + 0.1f,
                _groundLayer
            );
        }

        if (_isGrounded && _velocity.y < 0)
            _velocity.y = -2f;

        if (wasGrounded && !_isGrounded)
            _isJumping = true;

        if (!wasGrounded && _isGrounded)
            _isJumping = false;
    }

    private void HandleCrouch()
    {
        var keyboard = Keyboard.current;
        if (keyboard == null) return;

        // Toggle crouch on key press
        if (keyboard[_crouchKey].wasPressedThisFrame)
        {
            if (_isCrouching)
                TryStandUp();
            else
                CrouchDown();
        }

        // Smoothly transition controller height and camera eye height. These
        // are tracked as two independent settle checks — not just one gated
        // on _controller.height — because they're no longer derived from
        // each other and can finish lerping on different frames.
        bool heightSettled = Mathf.Approximately(_controller.height, _targetHeight);
        Vector3 camPos = _player.CameraRoot != null ? _player.CameraRoot.localPosition : Vector3.zero;
        bool cameraSettled = _player.CameraRoot == null || Mathf.Approximately(camPos.y, _targetCameraHeight);

        if (!heightSettled || !cameraSettled)
        {
            _controller.height = Mathf.Lerp(
                _controller.height,
                _targetHeight,
                Time.deltaTime * _crouchTransitionSpeed
            );

            // Keep controller grounded by adjusting center's Y only — X/Z are left
            // whatever they're set to on the CharacterController (e.g. a forward
            // offset), since this used to hardcode them to 0 and silently wipe out
            // any manual horizontal center offset the moment the player crouched.
            _controller.center = new Vector3(_controller.center.x, _controller.height / 2f, _controller.center.z);

            // Move camera root toward its crouch/stand target directly — no
            // longer derived from _controller.height (see _crouchCameraHeight
            // comment for why that coupling broke).
            if (_player.CameraRoot != null)
            {
                camPos.y = Mathf.Lerp(camPos.y, _targetCameraHeight, Time.deltaTime * _crouchTransitionSpeed);
                _player.CameraRoot.localPosition = camPos;
            }
        }
    }

    private void CrouchDown()
    {
        _isCrouching = true;
        _targetHeight = _crouchHeight;
        _targetCameraHeight = _crouchCameraHeight;
        _animator?.SetBool("IsCrouching", true);
    }

    private void TryStandUp()
    {
        // Check if there's room to stand. Exclude the Player layer — the cast
        // origin sits right at the top of the character's own crouched capsule,
        // and Physics.SphereCast (unlike Physics.Raycast) reports a hit when the
        // sphere already overlaps a collider at the start of the sweep. Without
        // this mask, the sphere was hitting the player's own CharacterController
        // and permanently blocking every stand-up attempt.
        Vector3 castOrigin = transform.position + Vector3.up * _crouchHeight;
        int obstructionMask = ~LayerMask.GetMask("Player");
        if (Physics.SphereCast(castOrigin, _controller.radius, Vector3.up, out _, _standHeight - _crouchHeight, obstructionMask))
        {
            // Something above — can't stand
            return;
        }

        _isCrouching = false;
        _targetHeight = _standHeight;
        _targetCameraHeight = _standCameraHeight;
        _animator?.SetBool("IsCrouching", false);
    }

    // Update HandleMovement to use crouch speed
    private void HandleMovement()
    {
        var keyboard = Keyboard.current;
        if (keyboard == null) return;

        float h = 0f;
        float v = 0f;

        if (keyboard.dKey.isPressed || keyboard.rightArrowKey.isPressed) h += 1f;
        if (keyboard.aKey.isPressed || keyboard.leftArrowKey.isPressed) h -= 1f;
        if (keyboard.wKey.isPressed || keyboard.upArrowKey.isPressed) v += 1f;
        if (keyboard.sKey.isPressed || keyboard.downArrowKey.isPressed) v -= 1f;

        bool hasInput = h != 0f || v != 0f;

        // Minigames use a fixed, scene-placed top-down camera — no
        // player-controlled look at all (see PlayerCamera.MiniGame /
        // GameRoomManager.FindActiveMinigameCamera). Directional input just
        // moves relative to that camera's fixed facing, and the body
        // auto-turns to face wherever it's currently moving. There's no
        // "strafing" once the body always faces where it's going, so —
        // unlike first-person — sprint and speed here don't gate on h == 0.
        bool isMinigameCam = _player.Camera != null &&
            _player.Camera.CurrentMode == PlayerCamera.CameraMode.MiniGame;

        bool wantsSprint = isMinigameCam
            ? keyboard.leftShiftKey.isPressed && hasInput && !_isCrouching
            : keyboard.leftShiftKey.isPressed && v > 0 && h == 0 && !_isCrouching;
        _isSprinting = wantsSprint && (!_staminaLimited || _currentStamina > 0f);

        if (_staminaLimited)
        {
            if (_isSprinting)
            {
                _currentStamina = Mathf.Max(0f, _currentStamina - _staminaDrainRate * Time.deltaTime);
                _regenDelayTimer = _staminaRegenDelay;
            }
            else if (_regenDelayTimer > 0f)
            {
                _regenDelayTimer -= Time.deltaTime;
            }
            else
            {
                _currentStamina = Mathf.Min(_maxStamina, _currentStamina + _staminaRegenRate * Time.deltaTime);
            }
        }

        // Cap speed when strafing while sprinting (first-person only — see
        // isMinigameCam comment above for why minigames skip the cap)
        float speed = _isCrouching ? _crouchSpeed :
                      isMinigameCam
                          ? (_isSprinting ? _sprintSpeed : _walkSpeed)
                          : (_isSprinting && h == 0 ? _sprintSpeed : _walkSpeed);

        Vector3 move;
        if (isMinigameCam)
        {
            // The scene camera looks steeply down, so its own "forward" is
            // mostly vertical and useless as a ground direction — its "up"
            // and "right" stay horizontal instead (no roll on these fixed
            // cameras), so those are what give us a ground-plane basis.
            Transform camT = _player.Camera.ActiveMiniGameCameraTransform;
            Vector3 camForward = camT != null
                ? Vector3.ProjectOnPlane(camT.up, Vector3.up).normalized
                : Vector3.forward;
            Vector3 camRight = camT != null
                ? Vector3.ProjectOnPlane(camT.right, Vector3.up).normalized
                : Vector3.right;

            move = camRight * h + camForward * v;
            if (move.sqrMagnitude > 1f) move.Normalize();

            if (hasInput)
            {
                Quaternion targetRot = Quaternion.LookRotation(move, Vector3.up);
                transform.rotation = Quaternion.RotateTowards(
                    transform.rotation, targetRot, _autoTurnSpeed * Time.deltaTime);
            }

            // Blend values: the body always faces its travel direction here,
            // so there's no strafe axis to feed — just an idle/walk/sprint
            // magnitude. (Best-guess mapping — I can't see the Animator
            // Controller's blend tree from here; adjust if it looks off.)
            _moveX = 0f;
            _moveY = !hasInput ? 0f : (_isSprinting ? 1f : 0.5f);
        }
        else
        {
            move = transform.right * h + transform.forward * v;
            _moveX = h;
            _moveY = _isSprinting ? v : v * 0.5f;
        }

        _controller.Move(move * speed * Time.deltaTime);
        _isMoving = hasInput;
    }

    public void SetStaminaLimited(bool limited)
    {
        _staminaLimited = limited;
        if (!limited) _currentStamina = _maxStamina;
    }

    private void HandleJump()
    {
        var keyboard = Keyboard.current;
        if (keyboard == null) return;

        // Debug.Log($"HandleJump — CurrentSeat: {_currentSeat?.name ?? "null"}, IsGrounded: {_isGrounded}");

        if (_standUpJumpBlockTimer > 0f)
        {
            _standUpJumpBlockTimer -= Time.deltaTime;
            return;
        }

        if (_jumpCooldown > 0f)
        {
            _jumpCooldown -= Time.deltaTime;
            return;
        }

        if (keyboard.spaceKey.wasPressedThisFrame && _isGrounded)
        {
            _velocity.y = Mathf.Sqrt(_jumpHeight * -2f * _gravity);
            _jumpCooldown = JumpCooldownDuration;
        }
    }

    private void ApplyGravity()
    {
        _velocity.y += _gravity * Time.deltaTime;
        _controller.Move(_velocity * Time.deltaTime);
    }

    /// Locks or unlocks movement under a named source. Multiple systems can
    /// hold a lock simultaneously (e.g. "sitting" and "stunned") — movement
    /// stays locked until every source has released it. Always pair a
    /// locked:true call with a matching locked:false call using the SAME
    /// source string.
    public void SetMovementLocked(bool locked, string source)
    {
        if (string.IsNullOrEmpty(source))
        {
            Debug.LogWarning("[PlayerMovement] SetMovementLocked called with no source — ignoring.");
            return;
        }

        if (locked)
            _movementLockSources.Add(source);
        else
            _movementLockSources.Remove(source);
    }

    /// Hard reset — clears every lock source regardless of who holds it.
    /// Use for full-state resets (minigame end, disconnect, return to lobby),
    /// not as a substitute for releasing a specific lock.
    public void ClearAllMovementLocks()
    {
        _movementLockSources.Clear();
    }

    public void SetMovementEnabled(bool enabled)
    {
        this.enabled = enabled;
    }

    private void HandleShove()
    {
        if (_shoveCooldown > 0f)
        {
            _shoveCooldown -= Time.deltaTime;
            return;
        }

        var keyboard = Keyboard.current;
        if (keyboard == null) return;

        if (keyboard[_shoveKey].wasPressedThisFrame)
        {
            _shoveCooldown = 3f;   // matches _shoveCooldown in LastOneStandingController
            GameRoomManager.Instance.RequestMinigameAction(
                "los_shove_request", _player.PlayerId.ToString());
        }
    }
}