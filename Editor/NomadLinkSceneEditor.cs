using UnityEditor;
using UnityEngine;

namespace Malloc.NomadLink
{
    [CustomEditor(typeof(NomadLinkScene))]
    public sealed class NomadLinkSceneEditor : UnityEditor.Editor
    {
        private NomadLinkSession Session => NomadLinkSession.Instance;

        private void OnEnable()
        {
            Session.InspectorStateChanged += Repaint;
        }

        private void OnDisable()
        {
            Session.InspectorStateChanged -= Repaint;
        }

        public override void OnInspectorGUI()
        {
            serializedObject.Update();
            var scene = (NomadLinkScene)target;
            var snapshot = Session.GetSnapshot(scene);

            using (new EditorGUI.DisabledScope(snapshot.Enabled))
            {
                EditorGUILayout.PropertyField(serializedObject.FindProperty("host"));
                EditorGUILayout.PropertyField(serializedObject.FindProperty("port"));
            }

            serializedObject.ApplyModifiedProperties();
            DrawSessionButton(scene, snapshot);
            EditorGUILayout.LabelField("Status", snapshot.Status);
            DrawSessionMessage(snapshot);

            if (snapshot.OwnsSession)
            {
                DrawMaterials(scene, snapshot.Rows);
            }
        }

        private void DrawSessionButton(
            NomadLinkScene scene,
            NomadLinkSession.SessionSnapshot snapshot)
        {
            if (snapshot.OwnsSession && snapshot.Enabled)
            {
                if (GUILayout.Button("Disable Sync"))
                {
                    Session.Disable(scene);
                }

                return;
            }

            using (new EditorGUI.DisabledScope(snapshot.OtherOwner))
            {
                if (GUILayout.Button("Enable Sync"))
                {
                    Session.Enable(scene, scene.Host, scene.Port);
                }
            }
        }

        private static void DrawSessionMessage(
            NomadLinkSession.SessionSnapshot snapshot)
        {
            if (!string.IsNullOrEmpty(snapshot.Message))
            {
                EditorGUILayout.HelpBox(snapshot.Message, snapshot.MessageType);
            }
        }

        private void DrawMaterials(
            NomadLinkScene scene,
            NomadLinkSession.MaterialRowSnapshot[] rows)
        {
            EditorGUILayout.Space();
            EditorGUILayout.LabelField(
                $"Synced Object Materials ({rows.Length})",
                EditorStyles.boldLabel);

            foreach (var row in rows)
            {
                var shortId = row.MeshId.Length <= 8
                    ? row.MeshId
                    : row.MeshId.Substring(0, 8);
                var label = new GUIContent(
                    $"{row.Name} ({shortId})",
                    row.MeshId);

                EditorGUI.BeginChangeCheck();
                var material = (Material)EditorGUILayout.ObjectField(
                    label,
                    row.Material,
                    typeof(Material),
                    false);
                if (EditorGUI.EndChangeCheck())
                {
                    Session.SetMaterial(scene, row.MeshId, material);
                }
            }
        }
    }
}
