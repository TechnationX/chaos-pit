// SingletonBehaviour.cs
// Place in: Assets/_Project/Scripts/Bootstrap/
// Generic base class for all persistent manager singletons.

using UnityEngine;

public class SingletonBehaviour<T> : MonoBehaviour where T : MonoBehaviour
{
    private static T _instance;

    public static T Instance
    {
        get
        {
            if (_instance == null)
            {
                Debug.LogError($"[SingletonBehaviour] {typeof(T).Name} instance is null. Is it in the Bootstrap scene?");
            }
            return _instance;
        }
    }

    protected virtual void Awake()
    {
        if (_instance != null && _instance != this)
        {
            Debug.LogWarning($"[SingletonBehaviour] Duplicate {typeof(T).Name} found. Destroying this one.");
            Destroy(gameObject);
            return;
        }

        _instance = this as T;
        DontDestroyOnLoad(gameObject);
    }
}