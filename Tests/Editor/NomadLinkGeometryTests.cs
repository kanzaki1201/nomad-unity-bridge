using System;
using System.Buffers.Binary;
using System.IO;
using System.Text;
using NUnit.Framework;
using UnityEngine;

namespace Malloc.NomadLink.Tests
{
    public sealed class NomadLinkGeometryTests
    {
        [Test]
        public void FrameParsingUsesBigEndianSizes()
        {
            const string json = "{\"type\":\"ping\",\"binary_size\":3}";
            var binary = new byte[] { 4, 5, 6 };
            var encoded = NomadLinkSession.EncodeFrame(json, binary);

            Assert.That(BinaryPrimitives.ReadUInt32BigEndian(
                encoded.AsSpan(0, 4)), Is.EqualTo(Encoding.UTF8.GetByteCount(json)));
            Assert.That(BinaryPrimitives.ReadUInt32BigEndian(
                encoded.AsSpan(4, 4)), Is.EqualTo(3));

            var parsed = NomadLinkSession.ReadFrame(new MemoryStream(encoded));
            Assert.That(Encoding.UTF8.GetString(parsed.Json), Is.EqualTo(json));
            Assert.That(parsed.Binary, Is.EqualTo(binary));
        }

        [Test]
        public void FrameParsingRejectsOversizedJson()
        {
            var prefix = new byte[8];
            BinaryPrimitives.WriteUInt32BigEndian(
                prefix.AsSpan(0, 4),
                (uint)NomadLinkSession.JsonLimit + 1u);

            Assert.Throws<InvalidDataException>(() =>
                NomadLinkSession.ReadFrame(new MemoryStream(prefix)));
        }

        [Test]
        public void CoordinateAndMatrixConversionNegateZ()
        {
            Assert.That(
                NomadLinkSession.ConvertPosition(new Vector3(1f, 2f, 3f)),
                Is.EqualTo(new Vector3(1f, 2f, -3f)));

            var values = Identity();
            values[12] = 4f;
            values[13] = 5f;
            values[14] = 6f;
            var matrix = NomadLinkSession.ConvertMatrix(values);

            Assert.That((Vector3)matrix.GetColumn(3),
                Is.EqualTo(new Vector3(4f, 5f, -6f)));
        }

        [Test]
        public void TriangleAndQuadConversionReverseWinding()
        {
            var faces = new[]
            {
                0, 1, 2, -1,
                0, 1, 2, 3
            };

            Assert.That(
                NomadLinkSession.TriangulateFaces(faces, 4),
                Is.EqualTo(new[]
                {
                    0, 2, 1,
                    0, 2, 1,
                    0, 3, 2
                }));
        }

        [Test]
        public void IndexSelectionChangesAbove65535Vertices()
        {
            Assert.That(NomadLinkSession.ShouldUseUInt32(65535), Is.False);
            Assert.That(NomadLinkSession.ShouldUseUInt32(65536), Is.True);
        }

        [Test]
        public void DeltaApplicationChangesOnlyNamedVertices()
        {
            var vertices = new[]
            {
                Vector3.zero,
                Vector3.one,
                Vector3.right
            };
            var replacement = new Vector3(7f, 8f, 9f);

            NomadLinkSession.ApplyPositionDelta(
                vertices, new[] { 1 }, new[] { replacement });

            Assert.That(vertices[0], Is.EqualTo(Vector3.zero));
            Assert.That(vertices[1], Is.EqualTo(replacement));
            Assert.That(vertices[2], Is.EqualTo(Vector3.right));
        }

        [Test]
        public void ObjectStateHeadersPreserveAbsentFields()
        {
            var state = NomadLinkSession.ObjectStateDto.Initial("Original");
            state = NomadLinkSession.PrepareObjectState(
                state,
                "{\"type\":\"mesh_full\",\"name\":\"Full\",\"visible\":false}")
                .State;
            state = NomadLinkSession.PrepareObjectState(
                state,
                "{\"type\":\"mesh_instance\",\"name\":\"Instance\"}")
                .State;
            var moved = Identity();
            moved[12] = 3f;
            var deltaJson =
                "{\"type\":\"mesh_delta\",\"visible\":true," +
                "\"world_matrix\":" + FloatArrayJson(moved) + "}";
            var update = NomadLinkSession.PrepareObjectState(state, deltaJson);

            Assert.That(update.State.name, Is.EqualTo("Instance"));
            Assert.That(update.State.visible, Is.True);
            Assert.That(update.Transform.Position, Is.EqualTo(new Vector3(3f, 0f, 0f)));
        }

        [Test]
        public void ShearedStateKeepsLastValidValues()
        {
            var state = NomadLinkSession.ObjectStateDto.Initial("Original");
            var matrix = Identity();
            matrix[4] = 0.25f;
            var json = "{\"name\":\"Changed\",\"world_matrix\":" +
                FloatArrayJson(matrix) + "}";

            var update = NomadLinkSession.PrepareObjectState(state, json);

            Assert.That(update.IsSheared, Is.True);
            Assert.That(update.State.name, Is.EqualTo("Original"));
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

        private static string FloatArrayJson(float[] values)
        {
            return "[" + string.Join(",", Array.ConvertAll(
                values,
                value => value.ToString(
                    "R", System.Globalization.CultureInfo.InvariantCulture))) + "]";
        }
    }
}
