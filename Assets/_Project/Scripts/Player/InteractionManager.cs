// InteractionManager.cs

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

    private PlayerObject _player;
    private IInteractable _currentTarget;
    private Camera _mainCamera;

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
        //Debug.Log($"IsHoldingObject: {_player.IsHoldingObject}");
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
                // New target found
                if (interactable != _currentTarget)
                {
                    _currentTarget = interactable;
                    ShowPrompt(_currentTarget.PromptLabel);
                }
                return;
            }
        }

        // Nothing hit — clear target
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
}