using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEditor;
using System.Collections;
using TMPro;
using System.Linq;
using UnityEngine.Events;
using System.Threading.Tasks;


namespace Bozo.ModularCharacters
{
    public class CharacterCreator : MonoBehaviour
    {
        [Header("Creator Dependencies")]
        public OutfitSystem character;
        public DecalController decalController;
        [SerializeField] Camera iconCamera;
        [SerializeField] RenderTexture iconTexture;
        public OutfitType[] outfitTypes;

        [Header("Outfit Dependencies")]
        private Dictionary<string, Outfit> OutfitDataBase = new Dictionary<string, Outfit>();
        [SerializeField] OutfitSelector outfitSelectorObject;
        private List<OutfitSelector> outfitSelectors = new List<OutfitSelector>();
        [SerializeField] Transform outfitContainer;
        [SerializeField] OutfitVisibilityToggler visibilityToggler;

        [Header("Texture Dependencies")]
        [SerializeField] TextureSelector textureSelectorObject;
        private List<TextureSelector> textureSelectors = new List<TextureSelector>();
        [SerializeField] Transform decalContainer;
        [SerializeField] Transform patternContainer;

        [Header("BodyShape Dependencies")]
        [SerializeField] BlendSlider blendSliderObject;
        private Dictionary<string, BlendSlider> blendSliders = new Dictionary<string, BlendSlider>();
        private Dictionary<string, BlendSlider> faceBlendSliders = new Dictionary<string, BlendSlider>();

        [Header("Unlock System")]
        public OutfitRegistry unlockRegistry;

        [SerializeField] BodyShapeSliders modSliderObject;
        private List<BodyShapeSliders> ModSliders = new List<BodyShapeSliders>();

        [SerializeField] Transform bodyShapeContainer;
        [SerializeField] Transform bodyModContainer;
        [SerializeField] Transform faceShapeContainer;
        [SerializeField] Transform faceModContainer;

        [SerializeField] string catagory;
        [SerializeField] GameObject currentPage;
        private GameObject previousPage;

        private Dictionary<string, List<GameObject>> outfits = new Dictionary<string, List<GameObject>>();
        private List<TexturePackage> textures = new List<TexturePackage>();

        [Header("UserSettings")]
        [SerializeField] GameObject RemoveButton;
        [SerializeField] string[] DisableRemoveByType;

        [Header("Save Dependencies")]
        [SerializeField] SaveSelector saveSelector;
        [SerializeField] Dictionary<string, SaveSelector> saveSlots = new Dictionary<string, SaveSelector>();
        [SerializeField] Transform saveContainer;
        [SerializeField] GameObject DeleteConfirmWindow;
        [SerializeField] TMP_Text loadedCharacterNameText;
        [SerializeField] TMP_Text DeleteCharacterNameText;

        //Events
        public UnityAction<string> onCategoryChanged;
        public UnityAction<GameObject> onCharacterChanged;
        public OutfitType type;

        [Header("Save Options")]
        public TMP_InputField CharacterName;

        [Header("Debug Options")]
        public string ShowOnlyPack;

        private void Awake()
        {
            outfits.Clear();
            OutfitDataBase.Clear();

            var ob = Resources.LoadAll<Outfit>("").ToList();
            var textureObjects = Resources.LoadAll<TexturePackage>("");

            ob.Sort((a, b) => string.Compare(a.PackName, b.PackName, System.StringComparison.OrdinalIgnoreCase));

            foreach (var item in ob)
            {
                if (!item.showCharacterCreator) continue;

                if (OutfitDataBase.ContainsKey(item.name))
                {
                    Debug.LogWarning($"Outfit: {item.name} has already been added, you may have a duplicate outfit in your project");
                }
                else
                {
                    OutfitDataBase.Add(item.name, item.GetComponent<Outfit>());
                }
            }
            foreach (var item in textureObjects)
            {
                textures.Add(item.GetComponent<TexturePackage>());
            }

            GenerateOutfitSelection();
            GenerateTextureSelection();
        }

        private void OnEnable()
        {
            character.OnOutfitChanged += OnOutfitUpdate;
            character.OnRigChanged += OnRigUpdate;
        }

        private void OnDisable()
        {
            character.OnOutfitChanged -= OnOutfitUpdate;
            character.OnRigChanged -= OnRigUpdate;
        }

        public void Start()
        {
            GetBodyBlends();
            GetBodyMods();
            SwitchCatagory("Top");

            UpdateCharacterSaves();
            onCharacterChanged?.Invoke(character.gameObject);
        }

        public void GenerateOutfitSelection()
        {
            if (outfitSelectorObject == null || outfitContainer == null) return;
            var outfits = OutfitDataBase.Values.ToArray();
            foreach (var item in outfits)
            {
                var selector = Instantiate(outfitSelectorObject, outfitContainer);
                selector.Init(item, this, unlockRegistry);
                outfitSelectors.Add(selector);
            }
        }

        public void GenerateTextureSelection()
        {
            if (textureSelectorObject == null) return;

            foreach (var item in textures)
            {
                Transform container = null;

                if (item.type == TextureType.Decal) container = decalContainer;
                if (item.type == TextureType.Pattern) container = patternContainer;

                if (container == null) continue;

                var selector = Instantiate(textureSelectorObject, container);
                selector.Init(item, this);
                textureSelectors.Add(selector);
            }
        }

        public void GetBodyBlends()
        {
            if (blendSliderObject == null || bodyModContainer == null) return;

            var shapes = character.GetShapes();
            var bodyShapes = character.GetBodyShapes();
            var faceShapes = character.GetFaceShapes();

            foreach (var item in faceBlendSliders.Values)
            {
                item.gameObject.SetActive(false);
            }

            foreach (var item in blendSliders.Values)
            {
                item.gameObject.SetActive(false);
            }

            foreach (var item in shapes)
            {

                if (bodyShapes.ContainsKey(item.Key))
                {
                    if (blendSliders.ContainsKey(item.Key))
                    {
                        blendSliders[item.Key].gameObject.SetActive(true);
                    }
                    else
                    {
                        var blendSlider = Instantiate(blendSliderObject, bodyShapeContainer);
                        blendSlider.Init(character, item.Key);
                        blendSliders.Add(item.Key, blendSlider);
                    }

                }
                else if (faceShapes.ContainsKey(item.Key))
                {
                    if (faceBlendSliders.ContainsKey(item.Key))
                    {
                        faceBlendSliders[item.Key].gameObject.SetActive(true);
                    }
                    else
                    {
                        var blendSlider = Instantiate(blendSliderObject, faceShapeContainer);
                        blendSlider.Init(character, item.Key);
                        faceBlendSliders.Add(item.Key, blendSlider);
                    }
                }
            }
        }

        public void GetBodyMods()
        {
            if (modSliderObject == null) return;

            var mods = character.GetMods().Values.ToList();

            for (int i = 0; i < ModSliders.Count; i++)
            {
                if(ModSliders[i] != null) Destroy(ModSliders[i].gameObject);
            }
            ModSliders.Clear();

            var container = bodyShapeContainer;
            foreach (var item in mods)
            {
                if (item.sorting == "Head") container = faceModContainer;
                else container = bodyModContainer;

                if (container == null) continue;

                var blendSlider = Instantiate(modSliderObject, container);
                blendSlider.Init(character, item);
                ModSliders.Add(blendSlider);
            }
        }

        public void OpenPage(GameObject page)
        {
            currentPage.SetActive(false);
            previousPage = currentPage;
            currentPage = page;
            currentPage.SetActive(true);
        }

        public void BackPage()
        {
            currentPage.SetActive(false);
            currentPage = previousPage;
            currentPage.SetActive(true);
            SwitchCatagory("");
        }

        public async void SetOutfit(Outfit outfit)
        {
            var inst = Instantiate(outfit, character.transform);
            await Task.Yield();
            SwitchCatagory(outfit.Type.name);
            type = outfit.Type;
        }

        public void OnOutfitUpdate(Outfit outfit)
        {
            if(visibilityToggler) visibilityToggler.Set(outfit);

            if (outfit == null) return;
            if (outfit.Type.name == "Head" || outfit.Type.name == "Body")
            {
                GetBodyBlends();
                GetBodyMods();
            }
        }

        public void OnRigUpdate(SkinnedMeshRenderer rig)
        {
            GetBodyBlends();
            GetBodyMods();
        }

        public void RemoveOutfit()
        {
            if (type == null) return;
            character.RemoveOutfit(type);
        }

        public void SwitchCatagory(string catagory)
        {
            foreach (var item in outfitSelectors)
            {
                if(item.outfit.requiredTags.Length != 0)
                {
                    foreach (var r in item.outfit.requiredTags)
                    {
                        if (!character.ContainsTag(r))
                        {
                            item.SetVisable("");
                        }
                        else
                        {
                            item.SetVisable(catagory);
                        }
                    }
                }
                else
                {
                    item.SetVisable(catagory);
                }

                if(ShowOnlyPack != "")
                {
                    if (!ShowOnlyPack.Contains(item.outfit.PackName)) item.SetVisable("x");
                }
            }

            if (RemoveButton != null) RemoveButton.SetActive(!DisableRemoveByType.Contains(catagory));

            var outfit = character.GetOutfit(catagory);

            //SetColorPickerObject(outfit);
            if(visibilityToggler) visibilityToggler.Set(outfit);

            if (outfit == null) return;

            SwitchTextureCatagory(outfit.TextureCatagory);
            type = outfit.Type;

            this.catagory = catagory;
            onCategoryChanged?.Invoke(catagory);
        }

        public void SwitchTextureCatagory(string catagory)
        {
            foreach (var item in textureSelectors)
            {
                item.SetVisable(catagory);
            }
        }

        public void ReplaceCharacter(OutfitSystem character)
        {
            this.character = character;
        }

        public string GetCurrentCatagory()
        {
            return catagory;
        }

        public void ToggleWalk(bool value)
        {
            character.animator.SetBool("isWalk", value);
        }

        public void SaveCharacter()
        {
            StartCoroutine(Save());
        }

        public Outfit GetOutfit(string outfitName)
        {
            return OutfitDataBase[outfitName];
        }

        [ContextMenu("Save")]
        private IEnumerator Save()
        {
            yield return new WaitForEndOfFrame();


            if (CharacterName == null)
            {
                Debug.LogWarning("Save Name InputField not Assigned Please assign one to save character", gameObject);
                yield break;
            }
            if (CharacterName.text.Length == 0)
            {
                Debug.LogWarning("Please enter in a name with at least one letter");
                yield break;
            }

            //Turning RenderTexture into a Proper Texture
            RenderTexture.active = iconTexture;
            Texture2D icon = new Texture2D(iconTexture.width, iconTexture.height, TextureFormat.RGBA32, false);
            Rect rect = new Rect(new Rect(0, 0, iconTexture.width, iconTexture.height));
            icon.ReadPixels(rect, 0, 0);
            icon.Apply();

            byte[] bytes = icon.EncodeToPNG();

            Texture2D characterIcon = null;


#if UNITY_EDITOR
            var settings = CharacterToolSettingsProvider.Get();
            var iconPath = $"{settings.iconFolder}/{CharacterName.text}";

            System.IO.File.WriteAllBytes(iconPath + ".png", bytes);
            AssetDatabase.Refresh();

            characterIcon = AssetDatabase.LoadAssetAtPath<Texture2D>(iconPath + ".png");
            TextureImporter importer = AssetImporter.GetAtPath(iconPath + ".png") as TextureImporter;

            if (importer != null)
            {
                importer.textureType = TextureImporterType.Sprite;
                importer.spriteImportMode = SpriteImportMode.Single;
                importer.SaveAndReimport();
            }
#endif

            if (!System.IO.Directory.Exists(BMAC_SaveSystem.iconFilePath))
            {
                System.IO.Directory.CreateDirectory(BMAC_SaveSystem.iconFilePath);
            }

            System.IO.File.WriteAllBytes(BMAC_SaveSystem.iconFilePath + "/" + CharacterName.text + ".png", bytes);
            BMAC_SaveSystem.SaveCharacter(character, CharacterName.text, characterIcon);

            UpdateCharacterSaves();
        }
        public void UpdateCharacterSaves()
        {
            if(saveContainer == null)
            {
                Debug.LogWarning("Save Container not assigned. Assigned one to display saves");
            }

            var saves = saveSlots.Values.ToArray();
            foreach (var item in saves)
            {
                Destroy(item.gameObject);
            }
            saveSlots.Clear();

            if (!System.IO.Directory.Exists(BMAC_SaveSystem.filePath))
            {
                System.IO.Directory.CreateDirectory(BMAC_SaveSystem.filePath);
                System.IO.Directory.CreateDirectory(BMAC_SaveSystem.iconFilePath);
                print("Created Save JSON save Location At: " + BMAC_SaveSystem.filePath);
            }

            var saveObjects = Resources.LoadAll<CharacterObject>("").ToList();

            for (int i = 0; i < saveObjects.Count; i++)
            {
                var ob = saveObjects[i];
                if (saveSlots.ContainsKey(ob.data.characterName))
                {
                    continue;
                }
                else
                {
                    var selector = Instantiate(saveSelector, saveContainer);
                    Sprite icon = null;
                    if (ob.icon != null)
                    {
                        icon = Sprite.Create(ob.icon, new Rect(0, 0, ob.icon.width, ob.icon.height), new Vector2(0.5f, 0.5f));
                    }
                    selector.Init(ob.data, icon, this);
                    saveSlots.Add(ob.data.characterName, selector);
                }
            }

            string path = BMAC_SaveSystem.filePath;
            List<string> jsonFiles = System.IO.Directory.GetFiles(path, "*.json").ToList();
            List<string> icons = System.IO.Directory.GetFiles(BMAC_SaveSystem.iconFilePath, "*.png").ToList();

            try
            {
                var animeFilePath = BMAC_SaveSystem.filePath.Replace("BoZo_StylizedModularCharacters/CustomCharacters", "BoZo_ModularAnimeCharacters/CharacterSaves");
                var animeIconFilePath = animeFilePath + "/Icons";
                if (System.IO.Directory.Exists(animeFilePath))
                {
                    jsonFiles.AddRange(System.IO.Directory.GetFiles(animeFilePath, "*.json"));
                    icons.AddRange(System.IO.Directory.GetFiles(animeIconFilePath, "*.png"));
                }
            }
            catch
            {

            }


            for (int i = 0; i < jsonFiles.Count; i++)
            {
                var json = System.IO.File.ReadAllText(jsonFiles[i]);
                var data = JsonUtility.FromJson<CharacterData>(json);
                var image = System.IO.File.ReadAllBytes(icons[i]);
                var texture = new Texture2D(2, 2);
                Sprite icon = null;

                if (saveSlots.ContainsKey(data.characterName)) continue;

                if (texture.LoadImage(image))
                {
                    icon = Sprite.Create(texture, new Rect(0, 0, texture.width, texture.height), new Vector2(0.5f, 0.5f));
                }

                var selector = Instantiate(saveSelector, saveContainer);
                selector.Init(data, icon, this);
                saveSlots.Add(data.characterName, selector);
            }


            SaveSelector[] items = saveContainer.GetComponentsInChildren<SaveSelector>(true)
                .Where(item => item.transform.parent == saveContainer)
                .OrderBy(item => item.data.characterName, System.StringComparer.OrdinalIgnoreCase)
                .ToArray();

            for (int i = 0; i < items.Length; i++)
            {
                items[i].transform.SetSiblingIndex(i);
            }

        }

        public void LoadCharacter(CharacterData data)
        {
            if (loadedCharacterNameText == null) return; loadedCharacterNameText.text = data.characterName;
            character.LoadCharacter(data);
        }

        public void DeleteCharacter()
        {
            if(DeleteConfirmWindow == null)
            {
                Debug.LogWarning("Delete Character Confirmation Window is no assigned please assign one to delete character", gameObject);
            }

            if (loadedCharacterNameText == null) return;
            if (loadedCharacterNameText.text == "") return;
            DeleteCharacterNameText.text = "Delete: " + loadedCharacterNameText.text;
            DeleteConfirmWindow.SetActive(true);
        }

        public void ConfirmDelete()
        {
            if (loadedCharacterNameText == null) return;
            BMAC_SaveSystem.DeleteCharacter(loadedCharacterNameText.text);
            loadedCharacterNameText.text = "";
            UpdateCharacterSaves();
        }

    }
}
