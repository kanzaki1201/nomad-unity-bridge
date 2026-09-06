using System;
using System.Buffers.Binary;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Threading;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Malloc.NomadLink.Tests
{
    public sealed class NomadLinkSessionTests
    {
        private NomadLinkSession session;
        private Scene scene;
        private GameObject ownerObject;
        private NomadLinkScene owner;
        private Material materialA;
        private Material materialB;
        private Material materialC;
        private int pairPort;

        [SetUp]
        public void SetUp()
        {
            session = NomadLinkSession.Instance;
            session.StopForTests();
            scene = EditorSceneManager.NewPreviewScene();
            ownerObject = new GameObject("Nomad Link Controller");
            SceneManager.MoveGameObjectToScene(ownerObject, scene);
            owner = ownerObject.AddComponent<NomadLinkScene>();
            CreateMaterials();
        }

        [TearDown]
        public void TearDown()
        {
            session.StopForTests();
            if (pairPort != 0)
            {
                EditorPrefs.DeleteKey(
                    NomadLinkSession.PairTokenKey("127.0.0.1", pairPort));
            }

            Destroy(materialA);
            Destroy(materialB);
            Destroy(materialC);
            if (ownerObject != null)
            {
                UnityEngine.Object.DestroyImmediate(ownerObject);
            }

            if (scene.IsValid())
            {
                EditorSceneManager.ClosePreviewScene(scene);
            }
        }

        [Test]
        public void SceneMarkerSerializesOnlyHostAndPort()
        {
            var fields = typeof(NomadLinkScene)
                .GetFields(BindingFlags.Instance | BindingFlags.Public |
                    BindingFlags.NonPublic | BindingFlags.DeclaredOnly)
                .Where(field => field.IsPublic ||
                    field.GetCustomAttribute<SerializeField>() != null)
                .Select(field => field.Name)
                .OrderBy(name => name)
                .ToArray();

            Assert.That(fields, Is.EqualTo(new[] { "host", "port" }));
        }

        [Test]
        public void EndToEndSessionPreservesObjectsMeshesAndMaterials()
        {
            using (var peer = new FakePeer())
            {
                pairPort = peer.Port;
                CompleteHandshake(peer, true);
                SendScene(peer);
                WaitUntil(() => session.ObjectCount == 3);

                var initial = session.GetSnapshot(owner);
                AssertDuplicateRowsAndSharedGeometry(initial);
                ClearSceneDirtiness();
                Assert.That(scene.isDirty, Is.False);
                Assert.That(session.SetMaterial(owner, "mesh-a", materialA), Is.True);
                Assert.That(session.SetMaterial(owner, "mesh-i", materialB), Is.True);
                Assert.That(scene.isDirty, Is.False);

                var rendererA = session.FindRenderer("mesh-a");
                var rendererI = session.FindRenderer("mesh-i");
                var meshA = session.FindMesh("mesh-a");
                Assert.That(rendererA.sharedMaterial, Is.SameAs(materialA));
                Assert.That(rendererI.sharedMaterial, Is.SameAs(materialB));
                Assert.That(session.FindMesh("mesh-i"), Is.SameAs(meshA));

                peer.Send(ObjectStateJson(
                    "mesh-a", "Renamed", true, true));
                peer.Send(MeshFullJson(
                    "mesh-a", "geometry-a", "Full Rename", false, true),
                    MeshBinary(2f));
                peer.Send(MeshDeltaJson("mesh-a", true), DeltaBinary(7f));
                WaitUntil(() =>
                    session.GetSnapshot(owner).Rows.Any(
                        row => row.Name == "Full Rename"));

                Assert.That(session.FindRenderer("mesh-a"), Is.SameAs(rendererA));
                Assert.That(session.FindMesh("mesh-a"), Is.SameAs(meshA));
                Assert.That(rendererA.sharedMaterial, Is.SameAs(materialA));
                Assert.That(meshA.vertices[1].x, Is.EqualTo(7f));

                Assert.That(session.SetMaterial(owner, "mesh-a", materialC), Is.True);
                Assert.That(session.SetMaterial(owner, "mesh-i", null), Is.True);
                Assert.That(rendererA.sharedMaterial, Is.SameAs(materialC));
                Assert.That(rendererI.sharedMaterial, Is.Null);
                Assert.That(scene.isDirty, Is.False);

                peer.Send(ObjectDeleteJson("mesh-b", true));
                WaitUntil(() => session.ObjectCount == 2);
                Assert.That(session.FindRenderer("mesh-a"), Is.SameAs(rendererA));
                Assert.That(session.FindRenderer("mesh-i"), Is.SameAs(rendererI));

                var secondOwnerObject = new GameObject("Second Controller");
                SceneManager.MoveGameObjectToScene(secondOwnerObject, scene);
                var secondOwner = secondOwnerObject.AddComponent<NomadLinkScene>();
                Assert.That(session.GetSnapshot(secondOwner).OtherOwner, Is.True);
                UnityEngine.Object.DestroyImmediate(secondOwnerObject);

                peer.Send(SessionConfigJson(3, true, "nomad", "client"));
                peer.Send(ObjectStateJson(
                    "mesh-a", "Blocked", true, true));
                PumpFor(0.2);
                Assert.That(Row("mesh-a").Name, Is.EqualTo("Full Rename"));
                peer.Send(ObjectStateJson(
                    "mesh-a", "Explicit Transfer", true, false));
                WaitUntil(() => Row("mesh-a").Name == "Explicit Transfer");

                Assert.That(session.SetMaterial(owner, "mesh-a", null), Is.True);
                Assert.That(rendererA.sharedMaterial, Is.Null);
                Assert.That(scene.isDirty, Is.False);

                peer.CloseConnection();
                WaitUntil(() => !session.IsRunning);
                Assert.That(session.GetSnapshot(owner).Rows, Is.Empty);
                Assert.That(scene.GetRootGameObjects().Any(
                    item => item.name == "Nomad Link Preview"), Is.False);
            }
        }

        [Test]
        public void UvSeamsSurviveDeltasInstancesAndFullReplacement()
        {
            using (var peer = new FakePeer())
            {
                pairPort = peer.Port;
                CompleteHandshake(peer, false);
                peer.Send(UvMeshJson(), UvMeshBinary());
                peer.Send(MeshInstanceJson("mesh-i", "geometry-a", "Instance", false));
                WaitUntil(() => session.ObjectCount == 2);
                var mesh = session.FindMesh("mesh-a");
                var renderer = session.FindRenderer("mesh-a");
                session.SetMaterial(owner, "mesh-a", materialA);
                session.SetMaterial(owner, "mesh-i", materialB);
                Assert.That(mesh.vertexCount, Is.EqualTo(5));
                Assert.That(mesh.triangles, Is.EqualTo(new[] { 0, 2, 1, 3, 4, 2 }));
                Assert.That(mesh.uv[0], Is.EqualTo(new Vector2(0.2f, 0.75f)));
                Assert.That(mesh.uv[3], Is.EqualTo(new Vector2(1.2f, -0.25f)));
                Assert.That(mesh.normals[0], Is.EqualTo(mesh.normals[3]));
                Assert.That(Vector3.Distance(mesh.normals[0], new Vector3(1f, 0f, -1f).normalized), Is.LessThan(0.0001f));
                var uv = mesh.uv;
                peer.Send(MeshDeltaJson("mesh-a", true)
                    .Replace("float32x3", "unsupported"), DeltaBinary(99f));
                WaitUntil(() => session.Status.Contains("Skipped"));
                Assert.That(mesh.uv, Is.EqualTo(uv));
                Assert.That(mesh.vertices[0].x, Is.Zero);
                var delta = DeltaBinary(7f);
                BinaryPrimitives.WriteUInt32LittleEndian(delta.AsSpan(0, 4), 0);
                peer.Send(MeshDeltaJson("mesh-a", true).Replace("\"vertex_count\":3", "\"vertex_count\":4"), delta);
                WaitUntil(() => !session.IsRunning || mesh.vertices[0].x == 7f);
                Assert.That(session.IsRunning, Is.True);
                Assert.That(mesh.vertices[3].x, Is.EqualTo(7f));
                Assert.That(mesh.normals[0], Is.EqualTo(mesh.normals[3]));
                Assert.That(mesh.uv, Is.EqualTo(uv));
                Assert.That(session.FindMesh("mesh-i"), Is.SameAs(mesh));
                peer.Send(UvMeshJson(), UvMeshBinary(0.4f));
                WaitUntil(() => mesh.uv[0].x == 0.4f);
                peer.Send(MeshFullJson("mesh-a", "geometry-a", "Cleared", false, false), MeshBinary(1f));
                WaitUntil(() => mesh.uv.Length == 0);
                Assert.That(session.FindMesh("mesh-a"), Is.SameAs(mesh));
                Assert.That(session.FindRenderer("mesh-a"), Is.SameAs(renderer));
                Assert.That(renderer.sharedMaterial, Is.SameAs(materialA));
                Assert.That(session.FindRenderer("mesh-i").sharedMaterial, Is.SameAs(materialB));
            }
        }

        [Test]
        public void TangentsFollowUvGeometryAndDisappearWithoutUvs()
        {
            using (var peer = new FakePeer())
            {
                pairPort = peer.Port;
                CompleteHandshake(peer, false);
                var full = MeshFullJson("mesh-a", "geometry-a", "Tangents", false, false);
                var uvFull = full.Replace("\"binary_size\":52", "\"binary_size\":92")
                    .TrimEnd('}') + ",\"texcoord_count\":3,\"texcoord_offset\":52," +
                    "\"texcoord_format\":\"float32x2\",\"face_uv_offset\":76}";
                var binary = new byte[92];
                MeshBinary(1f).CopyTo(binary, 0);
                WriteSingle(binary, 60, 1f);
                WriteSingle(binary, 72, 1f);
                BinaryPrimitives.WriteInt32LittleEndian(binary.AsSpan(80, 4), 1);
                BinaryPrimitives.WriteInt32LittleEndian(binary.AsSpan(84, 4), 2);
                peer.Send(uvFull, binary);
                WaitUntil(() => session.ObjectCount == 1);
                var mesh = session.FindMesh("mesh-a");
                AssertTangents(mesh, Vector3.right);
                var delta = DeltaBinary(1f);
                WriteVector(delta, 4, 1f, 0f, 1f);
                peer.Send(MeshDeltaJson("mesh-a", true), delta);
                WaitUntil(() => mesh.vertices[1].z == -1f);
                AssertTangents(mesh, new Vector3(1f, 0f, -1f).normalized);
                peer.Send(uvFull, binary);
                WaitUntil(() => mesh.vertices[1].z == 0f);
                AssertTangents(mesh, Vector3.right);
                peer.Send(full, MeshBinary(1f));
                WaitUntil(() => mesh.uv.Length == 0);
                Assert.That(mesh.HasVertexAttribute(UnityEngine.Rendering.VertexAttribute.Tangent), Is.False);
                peer.Send(MeshDeltaJson("mesh-a", true), DeltaBinary(2f));
                WaitUntil(() => mesh.vertices[1].x == 2f);
                Assert.That(mesh.HasVertexAttribute(UnityEngine.Rendering.VertexAttribute.Tangent), Is.False);
            }
        }

        private static void AssertTangents(Mesh mesh, Vector3 expected)
        {
            var tangents = mesh.tangents;
            Assert.That(tangents.Length, Is.EqualTo(mesh.vertexCount));
            foreach (var tangent in tangents)
            {
                Assert.That(Vector3.Distance((Vector3)tangent, expected), Is.LessThan(0.0001f));
                Assert.That(tangent.w, Is.EqualTo(1f));
            }
        }

        private static string UvMeshJson()
        {
            return MeshFullJson("mesh-a", "geometry-a", "UV", false, false)
                .Replace("\"vertex_count\":3", "\"vertex_count\":4")
                .Replace("\"face_count\":1", "\"face_count\":2")
                .Replace("\"face_offset\":36", "\"face_offset\":48")
                .Replace("\"binary_size\":52", "\"binary_size\":152")
                .TrimEnd('}') + ",\"texcoord_count\":5,\"texcoord_offset\":80," +
                "\"texcoord_format\":\"float32x2\",\"face_uv_offset\":120}";
        }

        [TestCase("missing-count")]
        [TestCase("missing-offset")]
        [TestCase("missing-format")]
        [TestCase("missing-faces")]
        [TestCase("range")]
        [TestCase("negative-index")]
        [TestCase("large-index")]
        [TestCase("nonfinite")]
        [TestCase("zero-count")]
        public void MalformedUvDataFailsClose(string kind)
        {
            using (var peer = new FakePeer())
            {
                pairPort = peer.Port;
                CompleteHandshake(peer, false);
                var json = UvMeshJson();
                var binary = UvMeshBinary();
                switch (kind)
                {
                    case "missing-count": json = json.Replace("\"texcoord_count\":5,", ""); break;
                    case "missing-offset": json = json.Replace("\"texcoord_offset\":80,", ""); break;
                    case "missing-format": json = json.Replace("\"texcoord_format\":\"float32x2\",", ""); break;
                    case "missing-faces": json = json.Replace(",\"face_uv_offset\":120", ""); break;
                    case "range": json = json.Replace("\"texcoord_offset\":80", "\"texcoord_offset\":150"); break;
                    case "zero-count": json = json.Replace("\"texcoord_count\":5", "\"texcoord_count\":0"); break;
                    case "nonfinite": WriteSingle(binary, 80, float.NaN); break;
                    case "negative-index": BinaryPrimitives.WriteInt32LittleEndian(binary.AsSpan(120, 4), -1); break;
                    case "large-index": BinaryPrimitives.WriteInt32LittleEndian(binary.AsSpan(120, 4), 5); break;
                }

                peer.Send(json, binary);
                WaitUntil(() => !session.IsRunning);
                Assert.That(session.Status, Is.EqualTo("Error"));
            }
        }

        [Test]
        public void ExpandedUvVertexCountSelectsUInt32Indices()
        {
            const int faceCount = 21846;
            const int uvCount = faceCount * 3;
            const int uvOffset = 36 + faceCount * 16;
            const int faceUvOffset = uvOffset + uvCount * 8;
            var binary = new byte[faceUvOffset + faceCount * 16];
            Array.Copy(MeshBinary(1f), binary, 36);
            for (var face = 0; face < faceCount; face++)
            {
                for (var corner = 0; corner < 4; corner++)
                {
                    var offset = face * 16 + corner * 4;
                    var vertex = corner == 3 ? -1 : corner;
                    var uv = corner == 3 ? -1 : face * 3 + corner;
                    BinaryPrimitives.WriteInt32LittleEndian(binary.AsSpan(36 + offset, 4), vertex);
                    BinaryPrimitives.WriteInt32LittleEndian(binary.AsSpan(faceUvOffset + offset, 4), uv);
                }
            }

            var json = MeshFullJson("mesh-a", "geometry-a", "Expanded", false, false)
                .Replace("\"face_count\":1", "\"face_count\":" + faceCount)
                .Replace("\"binary_size\":52", "\"binary_size\":" + binary.Length)
                .TrimEnd('}') + $",\"texcoord_count\":{uvCount},\"texcoord_offset\":{uvOffset}," +
                $"\"texcoord_format\":\"float32x2\",\"face_uv_offset\":{faceUvOffset}}}";
            using (var peer = new FakePeer())
            {
                pairPort = peer.Port;
                CompleteHandshake(peer, false);
                peer.Send(json, binary);
                WaitUntil(() => session.ObjectCount == 1);
                var mesh = session.FindMesh("mesh-a");
                Assert.That(mesh.vertexCount, Is.EqualTo(uvCount));
                Assert.That(mesh.indexFormat, Is.EqualTo(UnityEngine.Rendering.IndexFormat.UInt32));
            }
        }

        [TestCase(false)]
        [TestCase(true)]
        public void UvReplacementRequiresTopologyPermission(bool remove)
        {
            using (var peer = new FakePeer())
            {
                pairPort = peer.Port;
                CompleteHandshake(peer, false);
                peer.Send(UvMeshJson(), UvMeshBinary());
                WaitUntil(() => session.ObjectCount == 1);
                var json = UvMeshJson();
                if (remove)
                {
                    json = json.Substring(0, json.IndexOf(",\"texcoord_count\"", StringComparison.Ordinal)) + "}";
                }

                peer.Send(json.Replace("\"replace_topology\":true", "\"replace_topology\":false"), UvMeshBinary(0.5f));
                WaitUntil(() => !session.IsRunning);
                Assert.That(session.Error, Does.Contain("topology"));
            }
        }

        [Test]
        public void QuadUvsFollowCornersWithZeroOffset()
        {
            using (var peer = new FakePeer())
            {
                pairPort = peer.Port;
                CompleteHandshake(peer, false);
                var binary = UvMeshBinary();
                BinaryPrimitives.WriteInt32LittleEndian(binary.AsSpan(60, 4), 3);
                BinaryPrimitives.WriteInt32LittleEndian(binary.AsSpan(132, 4), 3);
                var json = UvMeshJson().Replace("\"face_count\":2", "\"face_count\":1")
                    .Replace("\"texcoord_offset\":80", "\"texcoord_offset\":0");
                peer.Send(json, binary);
                WaitUntil(() => session.ObjectCount == 1);
                var mesh = session.FindMesh("mesh-a");
                Assert.That(mesh.triangles, Is.EqualTo(new[] { 0, 2, 1, 0, 3, 2 }));
                Assert.That(mesh.uv.Length, Is.EqualTo(4));
                Assert.That(mesh.uv[0], Is.EqualTo(new Vector2(0f, 1f)));
            }
        }

        private static byte[] UvMeshBinary(float u = 0.2f)
        {
            var bytes = new byte[152];
            WriteVector(bytes, 0, 0f, 0f, 0f);
            WriteVector(bytes, 12, 1f, 0f, 0f);
            WriteVector(bytes, 24, 0f, 1f, 0f);
            WriteVector(bytes, 36, 0f, 0f, 1f);
            var faces = new[] { 0, 1, 2, -1, 0, 2, 3, -1 };
            var faceUvs = new[] { 0, 1, 2, 12345, 3, 2, 4, -1 };
            for (var index = 0; index < faces.Length; index++)
            {
                BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(48 + index * 4, 4), faces[index]);
                BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(120 + index * 4, 4), faceUvs[index]);
            }

            WriteSingle(bytes, 80, u);
            WriteSingle(bytes, 84, 0.25f);
            WriteSingle(bytes, 104, 1.2f);
            WriteSingle(bytes, 108, 1.25f);
            return bytes;
        }

        [Test]
        public void PaintUpdatesPreserveSeamsGeometryAndMaterials()
        {
            using (var peer = new FakePeer())
            {
                pairPort = peer.Port;
                CompleteHandshake(peer, false);
                peer.Send(UvMeshJson(), UvMeshBinary());
                peer.Send(MeshInstanceJson("mesh-i", "geometry-a", "Instance", false));
                WaitUntil(() => session.ObjectCount == 2);
                var mesh = session.FindMesh("mesh-a");
                session.SetMaterial(owner, "mesh-a", materialA);
                session.SetMaterial(owner, "mesh-i", materialB);
                var positions = mesh.vertices;
                var normals = mesh.normals;
                var tangents = mesh.tangents;
                var triangles = mesh.triangles;
                var uv = mesh.uv;
                var sparse = new byte[] { 0, 0, 0, 0, 255, 128, 0, 128, 64 };
                peer.Send(PaintJson("mesh_delta", 4, sparse.Length,
                    "\"count\":1,\"index_offset\":0,\"index_format\":\"uint32\",",
                    "\"color_offset\":4,\"color_format\":\"rgbm8\",\"opacity_offset\":8,\"opacity_format\":\"uint8_norm\""), sparse);
                WaitUntil(() => mesh.colors.Length == 5);
                Assert.That(mesh.colors[0].r, Is.EqualTo(128f / 255f).Within(0.00001f));
                Assert.That(mesh.colors[0].g, Is.EqualTo(128f * 128f / 65025f).Within(0.00001f));
                Assert.That(mesh.colors[0].a, Is.EqualTo(64f / 255f).Within(0.00001f));
                Assert.That(mesh.colors[3], Is.EqualTo(mesh.colors[0]));
                Assert.That(mesh.colors[1], Is.EqualTo(Color.white));
                var rgb = mesh.colors[0];
                peer.Send(PaintJson("mesh_attributes", 4, 4, "",
                    "\"opacity_offset\":0,\"opacity_format\":\"uint8_norm\""),
                    new byte[] { 255, 255, 255, 255 });
                WaitUntil(() => mesh.colors[0].a == 1f);
                Assert.That(mesh.colors[0].r, Is.EqualTo(rgb.r));
                Assert.That(mesh.vertices, Is.EqualTo(positions));
                Assert.That(mesh.normals, Is.EqualTo(normals));
                Assert.That(mesh.tangents, Is.EqualTo(tangents));
                Assert.That(mesh.triangles, Is.EqualTo(triangles));
                Assert.That(mesh.uv, Is.EqualTo(uv));
                Assert.That(session.FindMesh("mesh-i"), Is.SameAs(mesh));
                Assert.That(session.FindRenderer("mesh-a").sharedMaterial, Is.SameAs(materialA));
                Assert.That(session.FindRenderer("mesh-i").sharedMaterial, Is.SameAs(materialB));

                var mixed = DeltaBinary(7f).Concat(new byte[] { 0, 255, 0, 255 }).ToArray();
                var json = MeshDeltaJson("mesh-a", true).Replace("\"vertex_count\":3", "\"vertex_count\":4")
                    .Replace("\"binary_size\":16", "\"binary_size\":20");
                peer.Send(json.Insert(json.Length - 1, ",\"color_offset\":16,\"color_format\":\"rgbm8\""), mixed);
                WaitUntil(() => mesh.vertices[1].x == 7f);
                Assert.That(mesh.colors[1], Is.EqualTo(Color.green));
                Assert.That(mesh.colors[0].r, Is.EqualTo(rgb.r));
            }
        }

        [Test]
        public void FullPaintReplacesAndClearsOptionalChannels()
        {
            using (var peer = new FakePeer())
            {
                pairPort = peer.Port;
                CompleteHandshake(peer, false);
                var full = MeshFullJson("mesh-a", "geometry-a", "Paint", false, false);
                var binary = MeshBinary(1f);
                var rgb = Enumerable.Repeat(new byte[] { 255, 0, 0, 255 }, 3).SelectMany(x => x).ToArray();
                var json = full.Replace("\"binary_size\":" + binary.Length,
                    "\"binary_size\":" + (binary.Length + rgb.Length));
                peer.Send(json.Insert(json.Length - 1, ",\"color_offset\":" + binary.Length +
                    ",\"color_format\":\"rgbm8\""), binary.Concat(rgb).ToArray());
                WaitUntil(() => session.ObjectCount == 1);
                var mesh = session.FindMesh("mesh-a");
                Assert.That(mesh.colors, Is.EqualTo(new[] { Color.red, Color.red, Color.red }));
                var opacityJson = full.Replace("\"binary_size\":" + binary.Length,
                    "\"binary_size\":" + (binary.Length + 3));
                peer.Send(opacityJson.Insert(opacityJson.Length - 1, ",\"opacity_offset\":" + binary.Length +
                    ",\"opacity_format\":\"uint8_norm\""), binary.Concat(new byte[] { 0, 128, 255 }).ToArray());
                WaitUntil(() => mesh.colors[0].a == 0);
                Assert.That(mesh.colors[0], Is.EqualTo(new Color(1, 1, 1, 0)));
                peer.Send(full, binary);
                WaitUntil(() => mesh.colors.Length == 0);
            }
        }

        [TestCase("mesh_delta")]
        [TestCase("mesh_attributes")]
        [TestCase("mesh_full")]
        public void InvalidPaintPreservesWholeUpdateAndLaterRecovery(string type)
        {
            using (var peer = new FakePeer())
            {
                pairPort = peer.Port;
                CompleteHandshake(peer, false);
                SendScene(peer);
                WaitUntil(() => session.ObjectCount == 3);
                var mesh = session.FindMesh("mesh-a");
                var positions = mesh.vertices;
                session.SetMaterial(owner, "mesh-a", materialA);
                var binary = type == "mesh_full" ? MeshBinary(9f) : DeltaBinary(9f);
                var json = type == "mesh_full"
                    ? MeshFullJson("mesh-a", "geometry-a", "Wrong", true, false)
                    : MeshDeltaJson("mesh-a", true).Replace("mesh_delta", type);
                json = json.Insert(json.Length - 1,
                    ",\"color_offset\":9999,\"color_format\":\"rgbm8\"");
                peer.Send(json, binary);
                WaitUntil(() => session.Status.Contains("Skipped"));
                Assert.That(session.IsRunning, Is.True);
                Assert.That(mesh.vertices, Is.EqualTo(positions));
                Assert.That(mesh.colors, Is.Empty);
                Assert.That(Row("mesh-a").Name, Is.EqualTo("Duplicate"));
                Assert.That(session.FindRenderer("mesh-a").sharedMaterial, Is.SameAs(materialA));
                peer.Send(MeshDeltaJson("mesh-a", true), DeltaBinary(3f));
                WaitUntil(() => mesh.vertices[1].x == 3f);
            }
        }

        private static string PaintJson(string type, int vertices, int bytes, string indexFields, string channels)
        {
            return "{\"type\":\"" + type + "\",\"mesh_id\":\"mesh-a\",\"live_sync\":true," +
                "\"vertex_count\":" + vertices + ",\"binary_size\":" + bytes + "," + indexFields + channels + "}";
        }

        [TestCase("\"color_offset\":0")]
        [TestCase("\"color_format\":\"rgbm8\"")]
        [TestCase("\"opacity_offset\":0,\"opacity_format\":\"unknown\"")]
        [TestCase("\"color_offset\":-1,\"color_format\":\"rgbm8\"")]
        [TestCase("\"color_offset\":9223372036854775807,\"color_format\":\"rgbm8\"")]
        public void InvalidPaintChannelsRejectAtomically(string channels)
        {
            using (var peer = new FakePeer())
            {
                pairPort = peer.Port;
                CompleteHandshake(peer, false);
                SendScene(peer);
                WaitUntil(() => session.ObjectCount == 3);
                var mesh = session.FindMesh("mesh-a");
                peer.Send(PaintJson("mesh_attributes", 3, 12, "", channels), new byte[12]);
                WaitUntil(() => session.Status.Contains("Skipped"));
                Assert.That(session.IsRunning, Is.True);
                Assert.That(mesh.colors, Is.Empty);
                peer.Send(PaintJson("mesh_attributes", 3, 12, "",
                    "\"color_offset\":0,\"color_format\":\"rgbm8\""),
                    Enumerable.Repeat(new byte[] { 0, 255, 0, 255 }, 3).SelectMany(x => x).ToArray());
                WaitUntil(() => mesh.colors.Length == 3);
                Assert.That(mesh.colors[0], Is.EqualTo(Color.green));
            }
        }

        [TestCase("paint")]
        [TestCase("position")]
        [TestCase("index")]
        public void UnsupportedDeltaPreservesPreviewAndAcceptsLaterUpdates(string format)
        {
            using (var peer = new FakePeer())
            {
                pairPort = peer.Port;
                CompleteHandshake(peer, false);
                SendScene(peer);
                WaitUntil(() => session.ObjectCount == 3);
                session.SetMaterial(owner, "mesh-a", materialA);
                session.SetMaterial(owner, "mesh-i", materialB);
                var renderer = session.FindRenderer("mesh-a");
                var instance = session.FindRenderer("mesh-i");
                var mesh = session.FindMesh("mesh-a");
                var vertices = mesh.vertices;
                var triangles = mesh.triangles;

                var json = MeshDeltaJson("mesh-a", true);
                if (format == "paint")
                {
                    json = json.Replace("\"position_offset\":4", "\"color_offset\":4")
                        .Replace("\"position_format\":\"float32x3\"",
                            "\"color_format\":\"unknown\"")
                        .Replace("\"binary_size\":16", "\"binary_size\":8");
                }
                else
                {
                    json = json.Replace(format == "index" ? "uint32" : "float32x3",
                        "unknown");
                }

                json = json.Insert(json.Length - 1, ",\"name\":\"Skipped Rename\"");
                peer.Send(json, format == "paint"
                    ? DeltaBinary(9f).Take(8).ToArray() : DeltaBinary(9f));
                WaitUntil(() => session.Status.Contains("unsupported") || !session.IsRunning);
                Assert.That(session.IsRunning, Is.True);
                Assert.That(session.ObjectCount, Is.EqualTo(3));
                Assert.That(session.GetSnapshot(owner).Enabled, Is.True);
                Assert.That(session.Error, Is.Empty);
                Assert.That(session.FindRenderer("mesh-a"), Is.SameAs(renderer));
                Assert.That(session.FindRenderer("mesh-i"), Is.SameAs(instance));
                Assert.That(session.FindMesh("mesh-a"), Is.SameAs(mesh));
                Assert.That(session.FindMesh("mesh-i"), Is.SameAs(mesh));
                Assert.That(mesh.vertices, Is.EqualTo(vertices));
                Assert.That(mesh.triangles, Is.EqualTo(triangles));
                Assert.That(renderer.sharedMaterial, Is.SameAs(materialA));
                Assert.That(instance.sharedMaterial, Is.SameAs(materialB));
                Assert.That(Row("mesh-a").Name, Is.EqualTo("Duplicate"));

                peer.Send(MeshDeltaJson("mesh-a", true), DeltaBinary(7f));
                WaitUntil(() => mesh.vertices[1].x == 7f);
                Assert.That(session.IsRunning, Is.True);
                Assert.That(renderer.sharedMaterial, Is.SameAs(materialA));
                peer.Send(ObjectStateJson("mesh-a", "Continued", true, true));
                WaitUntil(() => Row("mesh-a").Name == "Continued");
            }
        }

        [Test]
        public void MalformedSupportedDeltaPreservesPreview()
        {
            using (var peer = new FakePeer())
            {
                pairPort = peer.Port;
                CompleteHandshake(peer, false);
                SendScene(peer);
                WaitUntil(() => session.ObjectCount == 3);
                peer.Send(MeshDeltaJson("mesh-a", true)
                    .Replace("\"position_offset\":4", "\"position_offset\":16"),
                    DeltaBinary(7f));
                WaitUntil(() => session.Status.Contains("Skipped"));
                Assert.That(session.Status, Does.Contain("range exceeds"));
                Assert.That(session.IsRunning, Is.True);
                Assert.That(session.ObjectCount, Is.EqualTo(3));
            }
        }

        [Test]
        public void DestroyedRendererFailsCloseAndClearsRows()
        {
            using (var peer = new FakePeer())
            {
                pairPort = peer.Port;
                CompleteHandshake(peer, false);
                peer.Send(MeshFullJson(
                    "mesh-a", "geometry-a", "Object", false, false),
                    MeshBinary(1f));
                WaitUntil(() => session.ObjectCount == 1);

                UnityEngine.Object.DestroyImmediate(
                    session.FindRenderer("mesh-a"));
                var snapshot = session.GetSnapshot(owner);

                Assert.That(session.IsRunning, Is.False);
                Assert.That(session.Status, Is.EqualTo("Error"));
                Assert.That(session.Error, Does.Contain("renderer"));
                Assert.That(snapshot.Rows, Is.Empty);
            }
        }

        private void CompleteHandshake(FakePeer peer, bool testEarlyFrame)
        {
            session.Enable(owner, "127.0.0.1", peer.Port);
            peer.WaitForConnection(session);
            var hello = JsonUtility.FromJson<HelloProbe>(
                peer.ReceiveJson(session));
            Assert.That(hello.type, Is.EqualTo("hello"));
            Assert.That(hello.protocol, Is.EqualTo(1));
            Assert.That(hello.capabilities, Is.EqualTo(new[]
            {
                "scene_edits",
                "object_state",
                "session_config",
                "mesh_instance",
                "mesh_delta_receive",
                "mesh_attributes_receive"
            }));

            peer.Send("{\"type\":\"pairing_pending\"}");
            WaitUntil(() => session.Status.Contains("pairing"));
            peer.Send(HelloReplyJson());
            WaitUntil(() => session.Status.Contains("configuration"));
            Assert.That(EditorPrefs.GetString(
                NomadLinkSession.PairTokenKey("127.0.0.1", peer.Port)),
                Is.EqualTo("pair-token"));

            peer.Send(SessionConfigJson(1, false, "auto", "none"));
            var config = JsonUtility.FromJson<SetConfigProbe>(
                peer.ReceiveJson(session));
            AssertSessionOverride(config);

            if (testEarlyFrame)
            {
                peer.Send(MeshFullJson(
                    "early", "geometry-early", "Early", true, true),
                    MeshBinary(1f));
                PumpFor(0.2);
                Assert.That(session.ObjectCount, Is.Zero);
            }

            peer.Send(SessionConfigJson(2, true, "nomad", "nomad"));
            var request = JsonUtility.FromJson<TypeProbe>(
                peer.ReceiveJson(session));
            Assert.That(request.type, Is.EqualTo("request_scene"));
            WaitUntil(() => session.Status == "Connected");
        }

        private static void AssertSessionOverride(SetConfigProbe config)
        {
            Assert.That(config.type, Is.EqualTo("set_session_config"));
            Assert.That(config.base_revision, Is.EqualTo(1));
            Assert.That(config.live_sync, Is.True);
            Assert.That(config.sync_mode, Is.EqualTo("nomad"));
            Assert.That(config.sync_objects, Is.True);
            Assert.That(config.sync_view, Is.True);
            Assert.That(config.sync_materials, Is.True);
            Assert.That(config.sync_lights, Is.False);
            Assert.That(config.sync_cameras, Is.True);
            Assert.That(config.sync_shading, Is.False);
            Assert.That(config.sync_postprocess, Is.True);
        }

        private void SendScene(FakePeer peer)
        {
            peer.Send(MeshFullJson(
                "mesh-a", "geometry-a", "Duplicate", false, false),
                MeshBinary(1f));
            peer.Send(MeshFullJson(
                "mesh-b", "geometry-b", "Other", false, false),
                MeshBinary(1f));
            peer.Send(MeshInstanceJson(
                "mesh-i", "geometry-a", "Duplicate", false));
        }

        private void AssertDuplicateRowsAndSharedGeometry(
            NomadLinkSession.SessionSnapshot snapshot)
        {
            Assert.That(snapshot.Rows.Length, Is.EqualTo(3));
            Assert.That(snapshot.Rows.Count(
                row => row.Name == "Duplicate"), Is.EqualTo(2));
            Assert.That(snapshot.Rows.Where(
                    row => row.Name == "Duplicate")
                .Select(row => row.MeshId),
                Is.EqualTo(new[] { "mesh-a", "mesh-i" }));
            Assert.That(session.FindRenderer("mesh-a"),
                Is.Not.SameAs(session.FindRenderer("mesh-i")));
        }

        private NomadLinkSession.MaterialRowSnapshot Row(string meshId)
        {
            return session.GetSnapshot(owner).Rows.Single(
                row => row.MeshId == meshId);
        }

        private void ClearSceneDirtiness()
        {
            var method = typeof(EditorSceneManager).GetMethod(
                "ClearSceneDirtiness",
                BindingFlags.Static | BindingFlags.Public |
                BindingFlags.NonPublic);
            Assert.That(method, Is.Not.Null);
            method.Invoke(null, new object[] { scene });
        }

        private void CreateMaterials()
        {
            var shader = Shader.Find("Hidden/InternalErrorShader") ??
                Shader.Find("Universal Render Pipeline/Lit");
            Assert.That(shader, Is.Not.Null);
            materialA = new Material(shader) { hideFlags = HideFlags.DontSave };
            materialB = new Material(shader) { hideFlags = HideFlags.DontSave };
            materialC = new Material(shader) { hideFlags = HideFlags.DontSave };
        }

        private static void Destroy(UnityEngine.Object value)
        {
            if (value != null)
            {
                UnityEngine.Object.DestroyImmediate(value);
            }
        }

        private void WaitUntil(Func<bool> condition)
        {
            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (!condition())
            {
                if (DateTime.UtcNow >= deadline)
                {
                    Assert.Fail("Timed out while waiting for the Nomad Link session.");
                }

                session.Pump();
                Thread.Sleep(10);
            }

            session.Pump();
        }

        private void PumpFor(double seconds)
        {
            var deadline = DateTime.UtcNow.AddSeconds(seconds);
            while (DateTime.UtcNow < deadline)
            {
                session.Pump();
                Thread.Sleep(10);
            }
        }

        private static string HelloReplyJson()
        {
            return JsonUtility.ToJson(new HelloReplyWire
            {
                type = "hello",
                protocol = 1,
                pair_token = "pair-token",
                capabilities = new[] { "scene_transfer", "session_config" }
            });
        }

        private static string SessionConfigJson(
            int revision,
            bool liveSync,
            string syncMode,
            string activeSource)
        {
            return JsonUtility.ToJson(new SessionConfigWire
            {
                type = "session_config",
                revision = revision,
                live_sync = liveSync,
                sync_mode = syncMode,
                active_source = activeSource,
                sync_view = true,
                sync_objects = liveSync,
                sync_materials = true,
                sync_lights = false,
                sync_cameras = true,
                sync_shading = false,
                sync_postprocess = true
            });
        }

        private static string MeshFullJson(
            string meshId,
            string geometryId,
            string name,
            bool liveSync,
            bool request)
        {
            return JsonUtility.ToJson(new MeshFullWire
            {
                type = "mesh_full",
                mesh_id = meshId,
                geometry_id = geometryId,
                name = name,
                vertex_count = 3,
                face_count = 1,
                binary_size = 52,
                coordinate_system = "nomad_y_up",
                world_matrix = Identity(),
                smooth_shading = true,
                live_sync = liveSync,
                replace_topology = true,
                position_offset = 0,
                position_format = "float32x3",
                face_offset = 36,
                face_format = "int32x4",
                request_id = request ? "request" : string.Empty
            });
        }

        private static string MeshInstanceJson(
            string meshId,
            string geometryId,
            string name,
            bool liveSync)
        {
            return JsonUtility.ToJson(new MeshInstanceWire
            {
                type = "mesh_instance",
                mesh_id = meshId,
                geometry_id = geometryId,
                name = name,
                visible = true,
                world_matrix = Identity(),
                live_sync = liveSync
            });
        }

        private static string MeshDeltaJson(string meshId, bool liveSync)
        {
            return JsonUtility.ToJson(new MeshDeltaWire
            {
                type = "mesh_delta",
                mesh_id = meshId,
                count = 1,
                vertex_count = 3,
                binary_size = 16,
                live_sync = liveSync,
                index_offset = 0,
                index_format = "uint32",
                position_offset = 4,
                position_format = "float32x3"
            });
        }

        private static string ObjectStateJson(
            string meshId,
            string name,
            bool visible,
            bool liveSync)
        {
            return JsonUtility.ToJson(new ObjectStateWire
            {
                type = "object_state",
                link_id = meshId,
                name = name,
                visible = visible,
                live_sync = liveSync
            });
        }

        private static string ObjectDeleteJson(string meshId, bool liveSync)
        {
            return JsonUtility.ToJson(new ObjectDeleteWire
            {
                type = "object_delete",
                link_id = meshId,
                live_sync = liveSync
            });
        }

        private static byte[] MeshBinary(float scale)
        {
            var bytes = new byte[52];
            WriteVector(bytes, 0, 0f, 0f, 0f);
            WriteVector(bytes, 12, scale, 0f, 0f);
            WriteVector(bytes, 24, 0f, scale, 0f);
            BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(36, 4), 0);
            BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(40, 4), 1);
            BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(44, 4), 2);
            BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(48, 4), -1);
            return bytes;
        }

        private static byte[] DeltaBinary(float x)
        {
            var bytes = new byte[16];
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(0, 4), 1);
            WriteVector(bytes, 4, x, 0f, 0f);
            return bytes;
        }

        private static void WriteVector(
            byte[] bytes,
            int offset,
            float x,
            float y,
            float z)
        {
            WriteSingle(bytes, offset, x);
            WriteSingle(bytes, offset + 4, y);
            WriteSingle(bytes, offset + 8, z);
        }

        private static void WriteSingle(byte[] bytes, int offset, float value)
        {
            BinaryPrimitives.WriteInt32LittleEndian(
                bytes.AsSpan(offset, 4),
                BitConverter.SingleToInt32Bits(value));
        }

        private static float[] Identity()
        {
            return new[]
            {
                1f, 0f, 0f, 0f,
                0f, 1f, 0f, 0f,
                0f, 0f, 1f, 0f,
                0f, 0f, 0f, 1f
            };
        }

        private sealed class FakePeer : IDisposable
        {
            private readonly TcpListener listener;
            private readonly Thread acceptThread;
            private volatile TcpClient client;
            private Exception acceptError;

            internal FakePeer()
            {
                listener = new TcpListener(IPAddress.Loopback, 0);
                listener.Start();
                Port = ((IPEndPoint)listener.LocalEndpoint).Port;
                acceptThread = new Thread(Accept)
                {
                    IsBackground = true,
                    Name = "Nomad Link test peer"
                };
                acceptThread.Start();
            }

            internal int Port { get; }

            internal void WaitForConnection(NomadLinkSession targetSession)
            {
                var deadline = DateTime.UtcNow.AddSeconds(5);
                while (client == null && acceptError == null)
                {
                    if (DateTime.UtcNow >= deadline)
                    {
                        Assert.Fail("Timed out while accepting the bridge connection.");
                    }

                    targetSession.Pump();
                    Thread.Sleep(10);
                }

                if (acceptError != null)
                {
                    throw acceptError;
                }
            }

            internal string ReceiveJson(NomadLinkSession targetSession)
            {
                var deadline = DateTime.UtcNow.AddSeconds(5);
                while (!client.GetStream().DataAvailable)
                {
                    if (DateTime.UtcNow >= deadline)
                    {
                        Assert.Fail("Timed out while waiting for a bridge frame.");
                    }

                    targetSession.Pump();
                    Thread.Sleep(10);
                }

                var frame = NomadLinkSession.ReadFrame(client.GetStream());
                return Encoding.UTF8.GetString(frame.Json);
            }

            internal void Send(string json, byte[] binary = null)
            {
                var frame = NomadLinkSession.EncodeFrame(
                    json, binary ?? Array.Empty<byte>());
                client.GetStream().Write(frame, 0, frame.Length);
            }

            internal void CloseConnection()
            {
                client?.Close();
            }

            public void Dispose()
            {
                client?.Close();
                listener.Stop();
                if (acceptThread.IsAlive)
                {
                    acceptThread.Join();
                }
            }

            private void Accept()
            {
                try
                {
                    client = listener.AcceptTcpClient();
                    client.NoDelay = true;
                }
                catch (Exception exception)
                {
                    acceptError = exception;
                }
            }
        }

        [Serializable]
        private class TypeProbe
        {
            public string type;
        }

        [Serializable]
        private sealed class HelloProbe : TypeProbe
        {
            public int protocol;
            public string[] capabilities;
        }

        [Serializable]
        private sealed class SetConfigProbe : TypeProbe
        {
            public int base_revision;
            public bool live_sync;
            public string sync_mode;
            public bool sync_view;
            public bool sync_objects;
            public bool sync_materials;
            public bool sync_lights;
            public bool sync_cameras;
            public bool sync_shading;
            public bool sync_postprocess;
        }

        [Serializable]
        private sealed class HelloReplyWire
        {
            public string type;
            public int protocol;
            public string pair_token;
            public string[] capabilities;
        }

        [Serializable]
        private sealed class SessionConfigWire
        {
            public string type;
            public int revision;
            public bool live_sync;
            public string sync_mode;
            public string active_source;
            public bool sync_view;
            public bool sync_objects;
            public bool sync_materials;
            public bool sync_lights;
            public bool sync_cameras;
            public bool sync_shading;
            public bool sync_postprocess;
        }

        [Serializable]
        private sealed class MeshFullWire
        {
            public string type;
            public string mesh_id;
            public string geometry_id;
            public string name;
            public int vertex_count;
            public int face_count;
            public int binary_size;
            public string coordinate_system;
            public float[] world_matrix;
            public bool smooth_shading;
            public bool live_sync;
            public bool replace_topology;
            public int position_offset;
            public string position_format;
            public int face_offset;
            public string face_format;
            public string request_id;
        }

        [Serializable]
        private sealed class MeshInstanceWire
        {
            public string type;
            public string mesh_id;
            public string geometry_id;
            public string name;
            public bool visible;
            public float[] world_matrix;
            public bool live_sync;
        }

        [Serializable]
        private sealed class MeshDeltaWire
        {
            public string type;
            public string mesh_id;
            public int count;
            public int vertex_count;
            public int binary_size;
            public bool live_sync;
            public int index_offset;
            public string index_format;
            public int position_offset;
            public string position_format;
        }

        [Serializable]
        private sealed class ObjectStateWire
        {
            public string type;
            public string link_id;
            public string name;
            public bool visible;
            public bool live_sync;
        }

        [Serializable]
        private sealed class ObjectDeleteWire
        {
            public string type;
            public string link_id;
            public bool live_sync;
        }
    }
}
