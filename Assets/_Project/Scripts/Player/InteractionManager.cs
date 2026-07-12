// InteractionManager.cs

using ChaosPit.Minigames.BombToss;
using ChaosPit.Minigames.Jinxed;
using ChaosPit.Minigames.ThiefsMarket;
using FishNet.Object;
using UnityEngine;
using UnityEngine.InputSystem;
using System.Collections;

public class InteractionManager : NetworkBehaviour
{
    [Header("Raycast Settings")]
    [SerializeField] private float _maxInteractDistance = 3f;
    [SerializeField] private LayerMask _interactableLayer;

    [Header("Prompt Settings")]
    [SerializeField] private GameObject _interactionPromptUI;
    [SerializeField] private TMPro.TextMeshProUGUI _promptLabel;

    [Header("Bomb Toss Pass")]
    [SerializeField] private float _passCheckDistance = 3f;
    [SerializeField] private float _passDirectionThreshold = 0.7f;
    [SerializeField] private float _passCooldown = 2f;
    private float _passCooldownUntil = 0f;

    [Header("Jinxed Tag")]
    [SerializeField] private float _tagCheckDistance = 3f;
    [SerializeField] private float _tagDirectionThreshold = 0.3f;
    private float _jinxedTagCooldownUntil = 0f;
    [SerializeField] private float _jinxedTagCooldown = 3f;

    [Header("Thiefs Market Punch")]
    [SerializeField] private float _punchCheckDistance = 3f;
    [SerializeField] private float _punchDirectionThreshold = 0.3f;
    [SerializeField] private float _localPunchCooldown = 1.5f;


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
    public float PunchCheckDistance => _punchCheckDistance;
    public float PunchCooldown => _localPunchCooldown;

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

        RestartUpdateLoop();
    }

    public void RestartUpdateLoop()
    {
        StopCoroutine("InteractionLoop");
        StartCoroutine("InteractionLoop");
    }

    private IEnumerator InteractionLoop()
    {
        while (true)
        {
            //Debug.Log($"[InteractionManager] Loop — IsOwner: {IsOwner}, enabled: {enabled}");
            if (IsOwner && enabled)
            {
                //Debug.Log($"[InteractionManager] Flags — bombPass: {_bombPassActive}, " +
                //    $"jinxedTag: {_jinxedTagActive}, " +
                //    $"tmPunch: {_tmPunchActive}, " +
                //    $"holding: {_player?.IsHoldingObject}");

                if (_bombPassActive)
                {
                    CheckForPassTarget();
                }
                else if (_jinxedTagActive)
                {
                    CheckForTagTarget();
                }
                else if (_tmPunchActive)
                {
                    //Debug.Log($"[TM-DEBUG] Loop reached punch branch, IsOwner: {IsOwner}");
                    CheckForPunchTarget();
                }
                else if (_player.IsHoldingObject)
                {
                    _currentTarget = null;
                    HidePrompt();

                    if (Mouse.current != null && Mouse.current.leftButton.wasPressedThisFrame)
                        _player.HeldObject.OnInteract(_player);
                }
                else
                {
                    CheckForInteractable();

                    if (_currentTarget != null && Mouse.current != null &&
                        Mouse.current.leftButton.wasPressedThisFrame)
                        TriggerInteraction();
                }
            }
            yield return null;
        }
    }

    private void Update()
    {

    }

    private void CheckForInteractable()
    {
        //Debug.Log($"[InteractionManager] CheckForInteractable fired");

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

        Camera cam = Camera.main;
        //Debug.Log($"[InteractionManager] Raycast — cam: {cam?.name}, " +
        //          $"pos: {cam?.transform.position}, " +
        //          $"forward: {cam?.transform.forward}, " +
        //          $"layerMask: {_interactableLayer.value}");

        Ray ray = new Ray(_mainCamera.transform.position, _mainCamera.transform.forward);
        RaycastHit hit;

        //Debug.Log($"[InteractionManager] Raycast — from: {ray.origin}, dir: {ray.direction}, " +
         //     $"maxDist: {_maxInteractDistance}, layerMask: {_interactableLayer.value}");

        if (Physics.Raycast(ray, out hit, _maxInteractDistance, _interactableLayer))
        {
            //Debug.Log($"[InteractionManager] Hit: {hit.collider.name}, layer: {hit.collider.gameObject.layer}");

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
        else
        {
            //Debug.Log($"[InteractionManager] No hit");
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
        if (!enabled)
        {
            //Debug.LogError($"[InteractionManager] DISABLED — full trace:");
            //Debug.LogError(new System.Exception("SetInteractionEnabled(false) caller").StackTrace);
        }

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
        if (active)
        {
            this.enabled = true;
            _passCooldownUntil = 0f;
        }
    }

    public void SetJinxedTagActive(bool active, JinxedHUD hud = null)
    {
        _jinxedTagActive = active;
        if (active) this.enabled = true;
        if (!active) _jinxedTagCooldownUntil = 0f;
    }

    public void SetThiefsMarketPunchActive(bool active, ThiefsMarketHUD hud = null)
    {
        _tmPunchActive = active;
        if (active) this.enabled = true;
        //Debug.Log($"[TM-DEBUG] SetThiefsMarketPunchActive — active: {active}, IsOwner: {IsOwner}, enabled: {enabled}, gameObject: {gameObject.name}");
    }

    private void CheckForPassTarget()
    {
        if (Mouse.current == null || !Mouse.current.leftButton.wasPressedThisFrame) return;
        if (Time.time < _passCooldownUntil) return;

        Vector3 facingDir = _player.transform.forward;
        PlayerObject bestTarget = null;
        float bestScore = _passDirectionThreshold;

        // Find all colliders on player layer within pass distance
        Collider[] hits = Physics.OverlapSphere(
            _player.transform.position, _passCheckDistance,
            Physics.AllLayers, QueryTriggerInteraction.Collide);

        //Debug.Log($"[PassCheck] no-mask hits: {hits.Length}");
        //foreach (Collider c in hits)
            //Debug.Log($"[PassCheck] hit: {c.name}, layer: {c.gameObject.layer}, layerName: {LayerMask.LayerToName(c.gameObject.layer)}");

        foreach (Collider col in hits)
        {
            if (!col.CompareTag("Player")) continue;

            PlayerObject target = col.GetComponentInParent<PlayerObject>();
            if (target == null || target.IsOwner) continue;

            Vector3 toTarget = (target.transform.position - _player.transform.position).normalized;
            float dot = Vector3.Dot(facingDir, toTarget);

            // Only consider players roughly in front (dot > 0)
            if (dot > bestScore)
            {
                bestScore = dot;
                bestTarget = target;
            }
        }

        if (bestTarget != null)
        {
            //Debug.Log($"[PassCheck] Passing to {bestTarget.PlayerId}");
            _passCooldownUntil = Time.time + _passCooldown;
            GameRoomManager.Instance.RequestMinigameAction(
                "bt_attempt_pass", bestTarget.Owner.ClientId.ToString());
        }
    }

    private void CheckForTagTarget()
    {
        Vector3 facingDir = _player.transform.forward;
        PlayerObject bestTarget = null;
        float bestScore = _tagDirectionThreshold;

        Collider[] hits = Physics.OverlapSphere(
            _player.transform.position, _tagCheckDistance,
            Physics.AllLayers, QueryTriggerInteraction.Collide);

        foreach (Collider col in hits)
        {
            if (!col.CompareTag("Player")) continue;

            PlayerObject target = col.GetComponentInParent<PlayerObject>();
            if (target == null || target.IsOwner) continue;

            Vector3 toTarget = (target.transform.position - _player.transform.position).normalized;
            float dot = Vector3.Dot(facingDir, toTarget);

            if (dot > bestScore)
            {
                bestScore = dot;
                bestTarget = target;
            }
        }

        if (bestTarget != null && Mouse.current != null && 
            Mouse.current.leftButton.wasPressedThisFrame && Time.time >= _jinxedTagCooldownUntil)
        {
            _jinxedTagCooldownUntil = Time.time + _jinxedTagCooldown;
            GameRoomManager.Instance.RequestMinigameAction(
                "jinxed_tag_attempt",
                $"{_player.Owner.ClientId}|{bestTarget.Owner.ClientId}");
        }

    }

    private void CheckForPunchTarget()
    {
        Vector3 facingDir = _player.transform.forward;
        PlayerObject bestTarget = null;
        float bestScore = _punchDirectionThreshold;

        Collider[] hits = Physics.OverlapSphere(
            _player.transform.position, _punchCheckDistance,
            Physics.AllLayers, QueryTriggerInteraction.Collide);

        foreach (Collider col in hits)
        {
            if (!col.CompareTag("Player")) continue;

            PlayerObject target = col.GetComponentInParent<PlayerObject>();
            if (target == null || target.IsOwner) continue;

            Vector3 toTarget = (target.transform.position - _player.transform.position).normalized;
            float dot = Vector3.Dot(facingDir, toTarget);

            if (dot > bestScore)
            {
                bestScore = dot;
                bestTarget = target;
            }
        }

        //Debug.Log($"[TM-DEBUG] Punch check — hits: {hits.Length}, bestTarget: {bestTarget?.name}");
        if (bestTarget != null)
        {
            if (Mouse.current != null && Mouse.current.leftButton.wasPressedThisFrame
                && Time.time >= _localPunchCooldownUntil)
            {
                //Debug.Log($"[TM-DEBUG] Sending punch request — target: {bestTarget.PlayerId}");
                _localPunchCooldownUntil = Time.time + _localPunchCooldown;
                GameRoomManager.Instance.RequestMinigameAction(
                    "tm_punch_request", bestTarget.Owner.ClientId.ToString());
            }
        }
        else
        {
        }
    }
}