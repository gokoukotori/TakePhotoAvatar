using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEditor.UIElements;
using UnityEngine;
using UnityEngine.UIElements;

namespace TakePhotoAvatar
{
    public sealed class AvatarCaptureWindow : EditorWindow
    {
        [SerializeField] private GameObject avatar;
        [SerializeField] private CaptureSettings settings = new CaptureSettings();
        [SerializeField] private string lastOutput;
        private readonly List<Texture2D> previews = new List<Texture2D>();
        private readonly List<Image> previewImages = new List<Image>();
        private HelpBox status;
        private Button previewButton;
        private Button saveButton;
        private Button revealButton;
        private bool isChangingPlayMode;

        private bool IsChangingPlayMode => isChangingPlayMode ||
            EditorApplication.isPlaying != EditorApplication.isPlayingOrWillChangePlaymode;

        [MenuItem("Tools/Gokoukotori/Take Photo Avatar/Aポーズ・顔アップ撮影ツール")]
        public static void Open()
        {
            var window = GetWindow<AvatarCaptureWindow>();
            window.titleContent = new GUIContent("Aポーズ・顔アップ撮影ツール");
            window.minSize = new Vector2(540, 660);
            if (window.avatar == null && AvatarCapture.ValidateAvatar(Selection.activeGameObject) == null)
            {
                window.avatar = Selection.activeGameObject;
                window.CreateGUI();
            }
            window.Show();
        }

        public void CreateGUI()
        {
            ClearPreviews();
            previewImages.Clear();
            rootVisualElement.Clear();
            string scriptPath = AssetDatabase.GetAssetPath(MonoScript.FromScriptableObject(this));
            StyleSheet sheet = AssetDatabase.LoadAssetAtPath<StyleSheet>((Path.GetDirectoryName(scriptPath) + "/AvatarCaptureWindow.uss").Replace('\\', '/'));
            // Keep the Editor theme: it supplies fonts, control sizes and ObjectField icon styles.
            if (sheet != null && !rootVisualElement.styleSheets.Contains(sheet)) rootVisualElement.styleSheets.Add(sheet);
            var scroll = new ScrollView();
            scroll.AddToClassList("capture-panel");
            rootVisualElement.Add(scroll);
            var heading = new Label("Aポーズ・顔アップ撮影ツール");
            heading.AddToClassList("heading");
            scroll.Add(heading);
            scroll.Add(new HelpBox("HumanoidのPrefabまたはシーン上のルートを指定します。プレイ中の衣装・表情を撮影する場合は、Hierarchyの実行中アバターを指定してください。どちらのモードでも腕を指定角度に調整します。撮影用の複製ではアニメーション・物理挙動を再実行しません。", HelpBoxMessageType.Info));
            var avatarField = new ObjectField("アバター") { name = "avatar", objectType = typeof(GameObject), allowSceneObjects = true, value = avatar };
            avatarField.RegisterValueChangedCallback(e => { avatar = (GameObject)e.newValue; Invalidate(); });
            scroll.Add(avatarField);
            scroll.Add(new Button(() => avatarField.value = Selection.activeGameObject) { text = "選択中のオブジェクトを使用" });

            settings.armAngle = Mathf.Clamp(settings.armAngle, 30, 90);
            AddSlider(scroll, "腕の角度（水平から下へ）", "armAngle", settings.armAngle, 30, 90, value => settings.armAngle = value);
            AddSlider(scroll, "全身の余白", "margin", settings.margin, 1.02f, 1.5f, value => settings.margin = value);
            AddSlider(scroll, "顔の拡大率", "faceZoom", settings.faceZoom, 0.5f, 2, value => settings.faceZoom = value);
            var offset = new Vector2Field("顔の中心位置（横・縦）") { name = "faceOffset", value = settings.faceOffset, tooltip = "顔の撮影中心を調整します。横の正の値は画像右方向、縦の正の値は上方向です。" };
            offset.RegisterValueChangedCallback(e => { settings.faceOffset = e.newValue; Invalidate(); });
            scroll.Add(offset);
            var color = new ColorField("背景色") { name = "background", value = settings.background, showAlpha = false, hdr = false };
            color.RegisterValueChangedCallback(e => { settings.background = e.newValue; Invalidate(); });
            color.SetEnabled(!settings.transparentBackground);
            var transparent = new Toggle("背景透過") { name = "transparentBackground", value = settings.transparentBackground, tooltip = "背景を透明にして、5枚すべてをアルファ付きPNGで保存します。" };
            transparent.RegisterValueChangedCallback(e =>
            {
                settings.transparentBackground = e.newValue;
                color.SetEnabled(!e.newValue);
                Invalidate();
            });
            scroll.Add(transparent);
            scroll.Add(color);
            var resolution = new PopupField<string>("画像サイズ", new List<string> { "512", "1024", "2048" }, settings.resolution.ToString()) { name = "resolution" };
            resolution.RegisterValueChangedCallback(e => settings.resolution = int.Parse(e.newValue));
            scroll.Add(resolution);

            var folderRow = new VisualElement();
            folderRow.AddToClassList("row");
            var folder = new TextField("保存先") { name = "outputDirectory", value = settings.outputDirectory, tooltip = "相対パスはこのUnityプロジェクトを基準にします。" };
            folder.AddToClassList("grow");
            folder.RegisterValueChangedCallback(e => settings.outputDirectory = e.newValue);
            folderRow.Add(folder);
            folderRow.Add(new Button(() =>
            {
                string initial;
                try { initial = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(Application.dataPath), settings.outputDirectory)); }
                catch { initial = Application.dataPath; }
                string chosen = EditorUtility.OpenFolderPanel("PNGの保存先", initial, "");
                if (!string.IsNullOrEmpty(chosen)) folder.value = chosen;
            }) { text = "選択…" });
            scroll.Add(folderRow);

            var actions = new VisualElement();
            actions.AddToClassList("row");
            previewButton = new Button(RefreshPreview) { text = "プレビュー更新", name = "previewButton" };
            saveButton = new Button(SaveImages) { text = "PNGを5枚保存", name = "saveButton" };
            previewButton.AddToClassList("grow");
            saveButton.AddToClassList("grow");
            actions.Add(previewButton);
            actions.Add(saveButton);
            scroll.Add(actions);
            status = new HelpBox("", HelpBoxMessageType.Info) { name = "status" };
            scroll.Add(status);

            var previewGrid = new VisualElement();
            previewGrid.AddToClassList("preview-grid");
            foreach (string label in new[] { "顔アップ", "正面", "右側面", "背面" })
            {
                var cell = new VisualElement();
                cell.AddToClassList("preview-cell");
                cell.Add(new Label(label));
                var image = new Image { scaleMode = ScaleMode.ScaleToFit };
                image.AddToClassList("preview-image");
                previewImages.Add(image);
                cell.Add(image);
                previewGrid.Add(cell);
            }
            scroll.Add(previewGrid);
            scroll.Add(new Label("正面・右側面・背面は同じ縮尺で撮影し、横に連結した三面図も保存します。プレビューは512pxです。") { name = "outputHint" });
            revealButton = new Button(() => EditorUtility.RevealInFinder(lastOutput)) { text = "保存したフォルダーを開く" };
            scroll.Add(revealButton);
            revealButton.SetEnabled(!string.IsNullOrEmpty(lastOutput) && Directory.Exists(lastOutput));
            Invalidate();
        }

        private void OnEnable()
        {
            EditorApplication.playModeStateChanged += OnPlayModeChanged;
        }

        private void OnDisable()
        {
            EditorApplication.playModeStateChanged -= OnPlayModeChanged;
            ClearPreviews();
        }

        private void OnPlayModeChanged(PlayModeStateChange state)
        {
            isChangingPlayMode = state == PlayModeStateChange.ExitingEditMode ||
                state == PlayModeStateChange.ExitingPlayMode;
            Invalidate();
        }

        private void AddSlider(VisualElement parent, string label, string name, float value, float min, float max, Action<float> set)
        {
            var field = new Slider(label, min, max) { name = name, value = value, showInputField = true };
            field.RegisterValueChangedCallback(e => { set(e.newValue); Invalidate(); });
            parent.Add(field);
        }

        private void Invalidate()
        {
            ClearPreviews();
            string error = AvatarCapture.ValidateAvatar(avatar);
            if (IsChangingPlayMode) error = "モードの切り替えが完了してから撮影してください。";
            previewButton?.SetEnabled(error == null);
            saveButton?.SetEnabled(error == null);
            if (status == null) return;
            status.text = error ?? "設定を変更したら「プレビュー更新」で画角を確認してください。";
            status.messageType = error == null ? HelpBoxMessageType.Info : HelpBoxMessageType.Warning;
        }

        public void RefreshPreview()
        {
            RunCapture(capture => UpdatePreviews(capture));
        }

        public void SaveImages()
        {
            RunCapture(capture =>
            {
                UpdatePreviews(capture);
                lastOutput = capture.Save(avatar.name);
                status.text = "PNGを5枚保存しました: " + lastOutput;
                revealButton.SetEnabled(true);
            });
        }

        private void RunCapture(Action<AvatarCapture> action)
        {
            if (IsChangingPlayMode) { Invalidate(); return; }
            try
            {
                using (var capture = new AvatarCapture(avatar, settings)) action(capture);
            }
            catch (Exception exception)
            {
                ClearPreviews();
                status.text = exception.Message;
                status.messageType = HelpBoxMessageType.Error;
                Debug.LogException(exception);
            }
        }

        private void UpdatePreviews(AvatarCapture capture)
        {
            ClearPreviews();
            for (int i = 0; i < previewImages.Count; i++)
            {
                Texture2D texture = capture.Render((CaptureView)i, 512);
                previews.Add(texture);
                previewImages[i].image = texture;
            }
            status.text = "プレビューを更新しました。顔の中心位置・拡大率を調整できます。";
            status.messageType = HelpBoxMessageType.Info;
        }

        private void ClearPreviews()
        {
            foreach (Image image in previewImages) image.image = null;
            foreach (Texture2D texture in previews) if (texture != null) DestroyImmediate(texture);
            previews.Clear();
        }
    }
}
