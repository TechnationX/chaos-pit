// PlayerMovement.cs

using UnityEngine;
using UnityEngine.InputSystem;
using FishNet.Object;

public class PlayerMovement : NetworkBehaviour
{
    [Header("Movement Settings")]
    [SerializeField] private float _walkSpeed = 4f;
    [SerializeField] private float _sprintSpeed = 8f;
    [SerializeField] private float _jumpHeight = 1.5f;
    [SerializeField] private float _gravity = -19.62f;

    [Header("Ground Check")]
    [SerializeField] private LayerMask _groundLayer;
    [SerializeField] private float _groundCheckDistance = 0.1f;

    [Header("Crouch Settings")]
    [SerializeField] private float _crouchSpeed = 2f;
    [SerializeField] private float _crouchHeight = 0.9f;
    [SerializeField] private float _standHeight = 1.8f;
    [SerializeField] private float _crouchTransitionSpeed = 10f;

    [Header("Animation")]
    private Animator _animator;

    [Header("Key Bindings")]
    [SerializeField] private Key _crouchKey = Key.C;

    private bool _isCrouching;
    private float _targetHeight;
    private bool _isJumping;

    private PlayerObject _player;
    private CharacterController _controller;
    private Vector3 _velocity;
    private bool _isGrounded;
    private bool _movementLocked;
    private bool _isSprinting;
    private Sittable _currentSeat;
    private bool _isMoving;
    public bool IsCrouching => _isCrouching;
    public bool IsJumping => _isJumping;
    private float _jumpCooldown = 0f;
    private const float JumpCooldownDuration = 0.2f;
    private float _moveX;
    private float _moveY;

    public bool IsMovementLocked => _movementLocked;
    public bool IsSprinting => _isSprinting;

    public void SetCurrentSeat(Sittable seat) => _currentSeat = seat;

    public void Initialize(PlayerObject player)
    {
        _player = player;
        _controller = player.GetComponent<CharacterController>();
        _targetHeight = _standHeight;
        _isGrounded = true;
        _animator?.SetBool("IsGrounded", true);
        _animator?.SetBool("IsJumping", false);

        // Find animator immediately on the CharacterModel
        _animator = player.CharacterModel.GetComponent<Animator>();
        if (_animator == null)
            _animator = player.CharacterModel.GetComponentInChildren<Animator>();

        Debug.Log($"[PlayerObject] Animator found: {_animator != null}, GameObject: {_animator?.gameObject.name}");
    }

    private void Update()
    {
        //Debug.Log($"Movement Update — IsOwner: {IsOwner}, Locked: {_movementLocked}");
        if (!IsOwner) return;

        if (_movementLocked)
        {
            HandleSeatExit();
            return;
        }

        HandleGroundCheck();
        HandleMovement();
        HandleJump();
        HandleCrouch();
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

        //Debug.Log($"IsGrounded: {_isGrounded}");
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

        // Smoothly transition controller height
        if (!Mathf.Approximately(_controller.height, _targetHeight))
        {
            _controller.height = Mathf.Lerp(
                _controller.height,
                _targetHeight,
                Time.deltaTime * _crouchTransitionSpeed
            );

            // Keep controller grounded by adjusting center
            _controller.center = new Vector3(0, _controller.height / 2f, 0);

            // Move camera root to match new height
            if (_player.CameraRoot != null)
                _player.CameraRoot.localPosition = new Vector3(0, _controller.height - 0.2f, 0);
        }
    }

    private void CrouchDown()
    {
        _isCrouching = true;
        _targetHeight = _crouchHeight;
        _animator?.SetBool("IsCrouching", true);
    }

    private void TryStandUp()
    {
        // Check if there's room to stand
        Vector3 castOrigin = transform.position + Vector3.up * _crouchHeight;
        if (Physics.SphereCast(castOrigin, _controller.radius, Vector3.up, out _, _standHeight - _crouchHeight))
        {
            // Something above — can't stand
            return;
        }

        _isCrouching = false;
        _targetHeight = _standHeight;
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

        _isSprinting = keyboard.leftShiftKey.isPressed && v > 0 && h == 0 && !_isCrouching;
        //Debug.Log($"isSprinting: {_isSprinting}, v: {v}, h: {h}, isCrouching: {_isCrouching}, shift: {keyboard.leftShiftKey.isPressed}");

        // Set blend values
        _moveX = h;
        _moveY = _isSprinting ? v : v * 0.5f; ;

        // Cap speed when strafing while sprinting
        float speed = _isCrouching ? _crouchSpeed :
                      _isSprinting && h == 0 ? _sprintSpeed : _walkSpeed;

        Vector3 move = transform.right * h + transform.forward * v;
        _controller.Move(move * speed * Time.deltaTime);

        _isMoving = (h != 0f || v != 0f);
    }

    private void HandleJump()
    {
        var keyboard = Keyboard.current;
        if (keyboard == null) return;

        // Debug.Log($"HandleJump — CurrentSeat: {_currentSeat?.name ?? "null"}, IsGrounded: {_isGrounded}");

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

    public void SetMovementLocked(bool locked)
    {
        _movementLocked = locked;
    }

    public void SetMovementEnabled(bool enabled)
    {
        this.enabled = enabled;
    }

}