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
                            "\"color_format\":\"rgbm8\"")
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
        public void MalformedSupportedDeltaStillFailsClose()
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
                WaitUntil(() => !session.IsRunning);
                Assert.That(session.Error, Does.Contain("range exceeds"));
                Assert.That(session.ObjectCount, Is.Zero);
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
                "mesh_delta_receive"
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
