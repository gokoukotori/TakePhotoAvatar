using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Animations;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace TakePhotoAvatar
{
    [Serializable]
    public sealed class CaptureSettings
    {
        public float armAngle = 45;
        public float margin = 1.12f;
        public float faceZoom = 1;
        public Vector2 faceOffset;
        public int resolution = 1024;
        public bool transparentBackground;
        public Color background = new Color(0.88f, 0.9f, 0.94f, 1);
        public string outputDirectory = "Captures";

        public void Validate()
        {
            if (!Finite(armAngle) || armAngle < 30 || armAngle > 90 ||
                !Finite(margin) || margin < 1.02f || margin > 1.5f ||
                !Finite(faceZoom) || faceZoom < 0.5f || faceZoom > 2 ||
                !Finite(faceOffset.x) || !Finite(faceOffset.y))
                throw new ArgumentException("ポーズまたは画角の設定値が範囲外です。");
            if (resolution != 512 && resolution != 1024 && resolution != 2048)
                throw new ArgumentException("解像度は512・1024・2048から選択してください。");
            if (!Finite(background.r) || !Finite(background.g) || !Finite(background.b))
                throw new ArgumentException("背景色が不正です。");
        }

        private static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
    }

    public enum CaptureView { Face, Front, Right, Back }

    // Owns only temporary objects. Never changes the selected prefab or scene object.
    public sealed class AvatarCapture : IDisposable
    {
        private Scene scene;
        private Camera camera;
        private Scene simulationScene;
        private GameObject container;
        private bool needsSimulation;
        private Renderer[] simulationRenderers;
        private bool[] renderingOff;
        private Vector3 faceCenter;
        private float faceSize;
        private readonly CaptureSettings settings;
        public Bounds Bounds { get; private set; }
        public GameObject Avatar { get; private set; }

        public static string ValidateAvatar(GameObject source)
        {
            if (source == null) return "Humanoidアバターのルートを指定してください。";
            Animator animator = source.GetComponent<Animator>();
            if (animator == null || animator.avatar == null || !animator.avatar.isValid || !animator.avatar.isHuman)
                return "ルートに有効なHumanoid Animatorが必要です。";
            Vector3 scale = source.transform.lossyScale;
            if (scale.x <= 0 || scale.y <= 0 || scale.z <= 0 ||
                Mathf.Abs(scale.x - scale.y) > scale.x * 0.001f || Mathf.Abs(scale.x - scale.z) > scale.x * 0.001f)
                return "アバターのスケールはXYZ共通の正の値にしてください。";
            return null;
        }

        public AvatarCapture(GameObject source, CaptureSettings settings)
        {
            string error = ValidateAvatar(source);
            if (error != null) throw new ArgumentException(error);
            settings.Validate();
            this.settings = JsonUtility.FromJson<CaptureSettings>(JsonUtility.ToJson(settings));
            if (SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null)
                throw new InvalidOperationException("撮影にはグラフィックスデバイスが必要です。");
            scene = EditorSceneManager.NewPreviewScene();
            try
            {
                container = new GameObject("TakePhotoAvatar Preview");
                SceneManager.MoveGameObjectToScene(container, scene);
                container.SetActive(false);
                needsSimulation = Application.isPlaying;
                if (needsSimulation)
                {
                    simulationScene = SceneManager.CreateScene("TakePhotoAvatar Simulation " + Guid.NewGuid().ToString("N"));
                    SceneManager.MoveGameObjectToScene(container, simulationScene);
                }
                Avatar = Object.Instantiate(source, container.transform, false);
                Avatar.name = source.name;
                Avatar.transform.SetLocalPositionAndRotation(Vector3.zero, Quaternion.identity);
                Avatar.transform.localScale = source.transform.lossyScale;
                foreach (Transform transform in Avatar.GetComponentsInChildren<Transform>(true))
                {
                    transform.gameObject.layer = 0;
                    if (GameObjectUtility.GetMonoBehavioursWithMissingScriptCount(transform.gameObject) != 0)
                        throw new InvalidOperationException("Missing Scriptがあります: " + transform.name);
                }
                foreach (Behaviour behaviour in Avatar.GetComponentsInChildren<Behaviour>(true))
                {
                    if (!needsSimulation || !IsCaptureDynamics(behaviour)) behaviour.enabled = false;
                    else if (behaviour.enabled) ValidateDynamicsTarget(behaviour);
                }
                foreach (Component component in Avatar.GetComponentsInChildren<Component>(true))
                    if (!needsSimulation && component is IConstraint constraint) constraint.constraintActive = false;
                foreach (Cloth cloth in Avatar.GetComponentsInChildren<Cloth>(true))
                    if (!needsSimulation) cloth.enabled = false;
                foreach (ParticleSystem particle in Avatar.GetComponentsInChildren<ParticleSystem>(true))
                    particle.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
                Avatar.SetActive(true);
                Animator animator = Avatar.GetComponent<Animator>();
                LowerArms(animator, settings.armAngle);
                if (needsSimulation)
                {
                    simulationRenderers = Avatar.GetComponentsInChildren<Renderer>(true);
                    renderingOff = simulationRenderers.Select(r => r.forceRenderingOff).ToArray();
                    foreach (Renderer renderer in simulationRenderers) renderer.forceRenderingOff = true;
                }
                container.SetActive(true);

                Renderer[] renderers = Avatar.GetComponentsInChildren<Renderer>()
                    .Where(r => r.enabled && r.gameObject.activeInHierarchy && !(r is ParticleSystemRenderer) && !(r is TrailRenderer)).ToArray();
                if (renderers.Length == 0) throw new InvalidOperationException("表示対象のメッシュがありません。");
                foreach (Material material in renderers.SelectMany(r => r.sharedMaterials).Distinct())
                    if (material == null || material.shader == null || !material.shader.isSupported || ShaderUtil.ShaderHasError(material.shader))
                        throw new InvalidOperationException("マテリアルまたはシェーダーに問題があります: " + (material == null ? "未設定" : material.name));
                UpdateFraming();

                var cameraObject = new GameObject("Capture Camera");
                SceneManager.MoveGameObjectToScene(cameraObject, scene);
                camera = cameraObject.AddComponent<Camera>();
                camera.enabled = false;
                camera.scene = scene;
                camera.orthographic = true;
                camera.aspect = 1;
                camera.clearFlags = CameraClearFlags.SolidColor;
                camera.backgroundColor = settings.transparentBackground ? Color.clear : new Color(settings.background.r, settings.background.g, settings.background.b, 1);
                camera.allowHDR = false;
                camera.allowMSAA = true;
                camera.cullingMask = 1;
                AddLight(new Vector3(25, 155, 0), 0.95f);
                AddLight(new Vector3(15, -30, 0), 0.55f);
            }
            catch { Dispose(); throw; }
        }

        public Texture2D Render(CaptureView view, int resolution)
        {
            if (camera == null) throw new ObjectDisposedException(nameof(AvatarCapture));
            if (needsSimulation) throw new InvalidOperationException("物理挙動の待機が完了していません。PrepareAsyncを待機してください。");
            if (resolution < 64 || resolution > 2048) throw new ArgumentOutOfRangeException(nameof(resolution));
            Vector3 target = Bounds.center;
            camera.orthographicSize = Mathf.Max(Bounds.extents.y, Mathf.Max(Bounds.extents.x, Bounds.extents.z)) * settings.margin;
            Vector3 direction = Vector3.forward;
            if (view == CaptureView.Face)
            {
                target = faceCenter + new Vector3(-settings.faceOffset.x, settings.faceOffset.y, 0) * faceSize;
                camera.orthographicSize = faceSize / settings.faceZoom;
            }
            else if (view == CaptureView.Right) direction = Vector3.left;
            else if (view == CaptureView.Back) direction = Vector3.back;
            float distance = Mathf.Max(4, Bounds.size.magnitude * 2);
            camera.transform.position = target + direction * distance;
            camera.transform.LookAt(target, Vector3.up);
            camera.nearClipPlane = Mathf.Max(0.001f, distance / 1000);
            camera.farClipPlane = distance * 3;
            // Resolve antialiasing before the sRGB conversion so edge colors and alpha agree.
            bool linear = QualitySettings.activeColorSpace == ColorSpace.Linear;
            var descriptor = new RenderTextureDescriptor(resolution, resolution, linear ? RenderTextureFormat.ARGBHalf : RenderTextureFormat.ARGB32, 24) { msaaSamples = 4, sRGB = false };
            descriptor.msaaSamples = SystemInfo.GetRenderTextureSupportedMSAASampleCount(descriptor);
            RenderTexture rt = RenderTexture.GetTemporary(descriptor);
            RenderTexture resolved = RenderTexture.GetTemporary(resolution, resolution, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
            RenderTexture previous = RenderTexture.active;
            bool previousSrgbWrite = GL.sRGBWrite;
            Texture2D texture = null;
            try
            {
                camera.targetTexture = rt;
                camera.Render();
                GL.sRGBWrite = linear;
                Graphics.Blit(rt, resolved);
                RenderTexture.active = resolved;
                texture = new Texture2D(resolution, resolution, settings.transparentBackground ? TextureFormat.RGBA32 : TextureFormat.RGB24, false);
                texture.ReadPixels(new Rect(0, 0, resolution, resolution), 0, 0);
                if (settings.transparentBackground) ConvertToStraightAlpha(texture);
                texture.Apply();
                return texture;
            }
            catch { if (texture != null) Object.DestroyImmediate(texture); throw; }
            finally
            {
                camera.targetTexture = null;
                RenderTexture.active = previous;
                GL.sRGBWrite = previousSrgbWrite;
                RenderTexture.ReleaseTemporary(resolved);
                RenderTexture.ReleaseTemporary(rt);
            }
        }

        private static void ConvertToStraightAlpha(Texture2D texture)
        {
            // Rendering onto transparent black (including MSAA) premultiplies RGB by alpha.
            // PNG and UI Toolkit Image expect straight alpha. Undo this in the rendering color space.
            bool linear = QualitySettings.activeColorSpace == ColorSpace.Linear;
            Color32[] pixels = texture.GetPixels32();
            for (int i = 0; i < pixels.Length; i++)
            {
                if (pixels[i].a == 255) continue;
                if (pixels[i].a == 0) { pixels[i] = new Color32(0, 0, 0, 0); continue; }
                Color color = pixels[i];
                float alpha = color.a;
                if (linear) color = color.linear;
                color.r /= alpha;
                color.g /= alpha;
                color.b /= alpha;
                if (linear) color = color.gamma;
                color.a = alpha;
                pixels[i] = color;
            }
            texture.SetPixels32(pixels);
        }

        public string Save(string avatarName)
        {
            settings.Validate();
            if (string.IsNullOrWhiteSpace(settings.outputDirectory)) throw new ArgumentException("保存先を指定してください。");
            string root = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(Application.dataPath), settings.outputDirectory));
            string safeName = string.Concat(avatarName.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
            if (string.IsNullOrWhiteSpace(safeName)) safeName = "Avatar";
            if (safeName.Length > 60) safeName = safeName.Substring(0, 60);
            string folder = Path.Combine(root, safeName + "_" + DateTime.Now.ToString("yyyyMMdd_HHmmss_fff") + "_" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(folder);
            Texture2D combined = null;
            bool complete = false;
            try
            {
                int resolution = settings.resolution;
                combined = new Texture2D(resolution * 3, resolution, settings.transparentBackground ? TextureFormat.RGBA32 : TextureFormat.RGB24, false);
                string[] names = { "face.png", "front.png", "right.png", "back.png" };
                for (int i = 0; i < names.Length; i++)
                {
                    Texture2D image = Render((CaptureView)i, resolution);
                    try
                    {
                        File.WriteAllBytes(Path.Combine(folder, names[i]), image.EncodeToPNG());
                        if (i > 0) combined.SetPixels32((i - 1) * resolution, 0, resolution, resolution, image.GetPixels32());
                    }
                    finally { Object.DestroyImmediate(image); }
                }
                combined.Apply();
                File.WriteAllBytes(Path.Combine(folder, "three-view.png"), combined.EncodeToPNG());
                complete = true;
                return folder;
            }
            finally
            {
                if (combined != null) Object.DestroyImmediate(combined);
                // Delete only this capture's own known files after a partial failure.
                if (!complete)
                {
                    foreach (string name in new[] { "face.png", "front.png", "right.png", "back.png", "three-view.png" })
                    {
                        try { File.Delete(Path.Combine(folder, name)); }
                        catch (IOException) { }
                        catch (UnauthorizedAccessException) { }
                    }
                    try { Directory.Delete(folder, false); }
                    catch (IOException) { }
                    catch (UnauthorizedAccessException) { }
                }
            }
        }

        private static void LowerArms(Animator animator, float angle)
        {
            LowerArm(animator, HumanBodyBones.LeftUpperArm, HumanBodyBones.LeftLowerArm, angle);
            LowerArm(animator, HumanBodyBones.RightUpperArm, HumanBodyBones.RightLowerArm, angle);
        }

        private static bool IsCaptureDynamics(Behaviour behaviour)
        {
            if (behaviour is IConstraint) return true;
            // Optional SDK support without a compile-time VRChat dependency.
            for (Type type = behaviour.GetType(); type != null; type = type.BaseType)
                if (type.FullName == "VRC.SDK3.Dynamics.PhysBone.Components.VRCPhysBone" ||
                    type.FullName == "VRC.SDK3.Dynamics.PhysBone.Components.VRCPhysBoneCollider" ||
                    type.FullName == "VRC.Dynamics.VRCConstraintBase") return true;
            return false;
        }

        private void ValidateDynamicsTarget(Behaviour behaviour)
        {
            if (behaviour is IConstraint) return;
            using (var serialized = new SerializedObject(behaviour))
            {
                foreach (string name in new[] { "rootTransform", "TargetTransform" })
                {
                    var property = serialized.FindProperty(name);
                    if (property == null || property.propertyType != SerializedPropertyType.ObjectReference) continue;
                    var target = property.objectReferenceValue as Transform;
                    if (target != null && !target.IsChildOf(Avatar.transform))
                        throw new InvalidOperationException("アバター外部を動かすConstraint・PhysBoneは撮影できません: " + behaviour.name);
                }
            }
        }

        public async Task PrepareAsync(CancellationToken cancellationToken = default)
        {
            if (!needsSimulation) return;
            double start = EditorApplication.timeSinceStartup;
            int firstFrame = Time.frameCount;
            while (EditorApplication.timeSinceStartup - start < 1 || Time.frameCount - firstFrame < 2)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (Avatar == null) throw new ObjectDisposedException(nameof(AvatarCapture));
                if (!Application.isPlaying) throw new OperationCanceledException();
                if (EditorApplication.isPaused || Time.timeScale <= 0)
                    throw new InvalidOperationException("Constraint・PhysBoneの更新のため、一時停止を解除して撮影してください。");
                if (EditorApplication.timeSinceStartup - start > 10)
                    throw new InvalidOperationException("物理挙動の更新を待機できませんでした。");
                await Task.Delay(16, cancellationToken);
            }
            cancellationToken.ThrowIfCancellationRequested();
            // Snapshot all views in one frame after the normal player loop has evaluated dynamics.
            foreach (Behaviour behaviour in Avatar.GetComponentsInChildren<Behaviour>(true)) behaviour.enabled = false;
            foreach (Component component in Avatar.GetComponentsInChildren<Component>(true))
                if (component is IConstraint constraint) constraint.constraintActive = false;
            SceneManager.MoveGameObjectToScene(container, scene);
            SceneManager.UnloadSceneAsync(simulationScene);
            simulationScene = default;
            for (int i = 0; i < simulationRenderers.Length; i++)
                if (simulationRenderers[i] != null) simulationRenderers[i].forceRenderingOff = renderingOff[i];
            needsSimulation = false;
            UpdateFraming();
        }

        private void UpdateFraming()
        {
            Bounds = Measure(Avatar.GetComponentsInChildren<Renderer>()
                .Where(r => r.enabled && !(r is ParticleSystemRenderer) && !(r is TrailRenderer)).ToArray());
            Animator animator = Avatar.GetComponent<Animator>();
            Transform head = Bone(animator, HumanBodyBones.Head);
            Transform leftEye = animator.GetBoneTransform(HumanBodyBones.LeftEye);
            Transform rightEye = animator.GetBoneTransform(HumanBodyBones.RightEye);
            faceSize = Bounds.size.y * (0.175f / 1.1974f);
            faceCenter = head.position + Vector3.up * (faceSize * 0.49f);
            if (leftEye != null && rightEye != null)
                faceCenter = (leftEye.position + rightEye.position) * 0.5f + Vector3.up * (faceSize / 7);
        }

        private static void LowerArm(Animator animator, HumanBodyBones upperBone, HumanBodyBones elbowBone, float angle)
        {
            Transform upper = Bone(animator, upperBone);
            Transform elbow = Bone(animator, elbowBone);
            Vector3 direction = elbow.position - upper.position;
            if (direction.sqrMagnitude < 0.00000001f)
                throw new InvalidOperationException("上腕と肘の位置が同じため、腕の向きを取得できません。");
            float side = Mathf.Sign(upper.position.x - Bone(animator, HumanBodyBones.Hips).position.x);
            float radians = angle * Mathf.Deg2Rad;
            Vector3 target = new Vector3(side * Mathf.Cos(radians), -Mathf.Sin(radians), 0);
            // Rotate only the upper arm. Child bones follow without changing elbow, wrist or finger pose.
            upper.rotation = Quaternion.FromToRotation(direction, target) * upper.rotation;
        }

        private static Transform Bone(Animator animator, HumanBodyBones bone)
        {
            Transform result = animator.GetBoneTransform(bone);
            if (result == null) throw new InvalidOperationException("必要なボーンがありません: " + bone);
            return result;
        }

        private static Bounds Measure(Renderer[] renderers)
        {
            Bounds bounds = default;
            bool initialized = false;
            foreach (Renderer renderer in renderers)
            {
                if (renderer is SkinnedMeshRenderer skinned)
                {
                    skinned.updateWhenOffscreen = true;
                    var mesh = new Mesh();
                    try
                    {
                        // Compensate for renderer scale so TransformPoint applies it only once.
                        skinned.BakeMesh(mesh, true);
                        foreach (Vector3 vertex in mesh.vertices)
                        {
                            Vector3 point = skinned.transform.TransformPoint(vertex);
                            if (!initialized) { bounds = new Bounds(point, Vector3.zero); initialized = true; }
                            else bounds.Encapsulate(point);
                        }
                    }
                    finally { Object.DestroyImmediate(mesh); }
                }
                else if (!initialized) { bounds = renderer.bounds; initialized = true; }
                else bounds.Encapsulate(renderer.bounds);
            }
            if (!initialized || bounds.size.y < 0.001f) throw new InvalidOperationException("撮影範囲を取得できません。");
            return bounds;
        }

        private void AddLight(Vector3 rotation, float intensity)
        {
            var lightObject = new GameObject("Capture Light");
            SceneManager.MoveGameObjectToScene(lightObject, scene);
            lightObject.transform.rotation = Quaternion.Euler(rotation);
            Light light = lightObject.AddComponent<Light>();
            light.type = LightType.Directional;
            light.intensity = intensity;
            light.cullingMask = 1;
            light.shadows = LightShadows.None;
        }

        public void Dispose()
        {
            if (container != null) Object.DestroyImmediate(container);
            container = null;
            if (simulationScene.IsValid() && simulationScene.isLoaded) SceneManager.UnloadSceneAsync(simulationScene);
            simulationScene = default;
            if (scene.IsValid()) EditorSceneManager.ClosePreviewScene(scene);
            scene = default;
            camera = null;
            Avatar = null;
        }
    }
}
