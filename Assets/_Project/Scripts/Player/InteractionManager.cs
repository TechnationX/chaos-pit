// InteractionManager.cs

using ChaosPit.Minigames.BombToss;
using ChaosPit.Minigames.Jinxed;
using ChaosPit.Minigames.Template;
using ChaosPit.Minigames.ThiefsMarket;
using FishNet.Object;
using UnityEngine;
using UnityEngine.InputSystem;

public class InteractionManager : NetworkBehaviour
{
    [Header("Raycast Settings")]
    [SerializeField] private float _maxInteractDistance = 3f;
    [SerializeField] private LayerMask _interactableLayer;

    [Header("Prompt Settings")]
    [SerializeField] private GameObject _interactionPromptUI;
    [SerializeField] private TMPro.TextMeshProUGUI _promptLabel;

    [Header("Bomb Toss Pass")]
    [SerializeField] private float _passCheckDistance = 5f;
    [SerializeField] private LayerMask _playerLayer;

    [Header("Jinxed Tag")]
    [SerializeField] private float _tagCheckDistance = 2.5f;

    [Header("Thiefs Market Punch")]
    [SerializeField] private float _punchCheckDistance = 2.5f;
    [SerializeField] private float _localPunchCooldownDisplay = 1.5f;

    private PlayerObject _player;
    private IInteractable _currentTarget;
    private Camera _mainCamera;

    private bool _bombPassActive = false;
    private BombTossHUD _bombTossHUD;

    private bool _jinxedTagActive = false;
    private JinxedHUD _jinxedHUD;

    private bool _tmPunchActive = false;
    private ThiefsMarketHUD _tmHUD;
    private float _localPunchCooldownUntil = 0f;

    public void Initialize(PlayerObject player)
    {
        _player = player;
        _mainCamera = Camera.main;

        // Find UI in scene if not manually assigned
        if (_interactionPromptUI == null)
            _interactionPromptUI = GameObject.Find("InteractionPrompt");

        if (_promptLabel == null && _interactionPromptUI != null)
            _promptLabel = _interactionPromptUI.GetComponentInChildren<TMPro.TextMeshProUGUI>();

        if (_interactionPromptUI != null)
            _interactionPromptUI.SetActive(false);
    }

    private void Update()
    {
        if (!IsOwner) return;

        if (_bombPassActive)
        {
            CheckForPassTarget();
            return; // skip normal interaction while bomb pass is active
        }

        if (_jinxedTagActive)
        {
            CheckForTagTarget();
            return;
        }

        if (_tmPunchActive)
        {
            CheckForPunchTarget();
            return;
        }

        // Handle drop while holding
        if (_player.IsHoldingObject)
        {
            _currentTarget = null;
            HidePrompt();

            if (Mouse.current != null && Mouse.current.leftButton.wasPressedThisFrame)
                _player.HeldObject.OnInteract(_player);
            return;
        }

        CheckForInteractable();

        if (_currentTarget != null && Mouse.current != null && Mouse.current.leftButton.wasPressedThisFrame)
            TriggerInteraction();
    }

    private void CheckForInteractable()
    {
        // Block interaction if already holding an object
        if (_player.IsHoldingObject)
        {
            if (_currentTarget != null)
            {
                _currentTarget = null;
                HidePrompt();
            }
            return;
        }

        Ray ray = new Ray(_mainCamera.transform.position, _mainCamera.transform.forward);
        RaycastHit hit;

        if (Physics.Raycast(ray, out hit, _maxInteractDistance, _interactableLayer))
        {
            IInteractable interactable = hit.collider.GetComponentInParent<IInteractable>();

            if (interactable != null)
            {
                if (interactable != _currentTarget)
                {
                    _currentTarget = interactable;
                    ShowPrompt(_currentTarget.PromptLabel);
                }
                return;
            }
        }

        if (_currentTarget != null)
        {
            _currentTarget = null;
            HidePrompt();
        }
    }

    private void TriggerInteraction()
    {
        _currentTarget.OnInteract(_player);
    }

    private void ShowPrompt(string label)
    {
        if (_interactionPromptUI == null) return;

        _promptLabel.text = label;
        _interactionPromptUI.SetActive(true);
    }

    private void HidePrompt()
    {
        if (_interactionPromptUI == null) return;

        _interactionPromptUI.SetActive(false);
    }

    // Called by MiniGameController to disable interaction in mini games
    public void SetInteractionEnabled(bool enabled)
    {
        this.enabled = enabled;

        if (!enabled)
        {
            _currentTarget = null;
            HidePrompt();
        }
    }

    public void SetBombPassActive(bool active, BombTossHUD hud = null)
    {
        _bombPassActive = active;
        _bombTossHUD = hud;

        if (!active)
            _bombTossHUD?.SetPassPrompt(false);
    }

    public void SetJinxedTagActive(bool active, JinxedHUD hud = null)
    {
        _jinxedTagActive = active;
        _jinxedHUD = hud;

        if (!active)
            _jinxedHUD?.SetTagPrompt(false);
    }

    public void SetThiefsMarketPunchActive(bool active, ThiefsMarketHUD hud = null)
    {
        _tmPunchActive = active;
        _tmHUD = hud;

        if (!active)
            _tmHUD?.SetPunchPrompt(false);
    }

    private void CheckForPassTarget()
    {
        Ray ray = new Ray(_mainCamera.transform.position, _mainCamera.transform.forward);

        if (Physics.Raycast(ray, out RaycastHit hit, _passCheckDistance, _playerLayer))
        {
            PlayerObject target = hit.collider.GetComponentInParent<PlayerObject>();

            if (target != null && !target.IsOwner)
            {
                string name = target.name;
                _bombTossHUD?.SetPassPrompt(true, name);

                if (Mouse.current != null && Mouse.current.leftButton.wasPressedThisFrame)
                {
                    GameRoomManager.Instance.RequestMinigameAction(
                        "bt_attempt_pass", target.PlayerId.ToString());

                    _bombTossHUD?.SetPassPrompt(false);
                }
                return;
            }
        }

        _bombTossHUD?.SetPassPrompt(false);
    }

    private void CheckForTagTarget()
    {
        Ray ray = new Ray(_mainCamera.transform.position, _mainCamera.transform.forward);

        if (Physics.Raycast(ray, out RaycastHit hit, _tagCheckDistance, _playerLayer))
        {
            PlayerObject target = hit.collider.GetComponentInParent<PlayerObject>();

            if (target != null && !target.IsOwner)
            {
                _jinxedHUD?.SetTagPrompt(true, target.PlayerName);

                if (Mouse.current != null && Mouse.current.leftButton.wasPressedThisFrame)
                {
                    GameRoomManager.Instance.RequestMinigameAction(
                        "jinxed_tag_attempt",
                        $"{_player.PlayerId}|{target.PlayerId}");

                    _jinxedHUD?.SetTagPrompt(false);
                }
                return;
            }
        }

        _jinxedHUD?.SetTagPrompt(false);
    }

    private void CheckForPunchTarget()
    {
        Ray ray = new Ray(_mainCamera.transform.position, _mainCamera.transform.forward);

        if (Physics.Raycast(ray, out RaycastHit hit, _punchCheckDistance, _playerLayer))
        {
            PlayerObject target = hit.collider.GetComponentInParent<PlayerObject>();

            if (target != null && !target.IsOwner)
            {
                _tmHUD?.SetPunchPrompt(true, target.PlayerName);

                if (Mouse.current != null && Mouse.current.leftButton.wasPressedThisFrame
                    && Time.time >= _localPunchCooldownUntil)
                {
                    _localPunchCooldownUntil = Time.time + _localPunchCooldownDisplay;

                    GameRoomManager.Instance.RequestMinigameAction(
                        "tm_punch_request", target.PlayerId.ToString());
                }
                return;
            }
        }

        _tmHUD?.SetPunchPrompt(false);
    }
}