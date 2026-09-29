using System;
using System.Threading;
using NUnit.Framework;

namespace XRCollab.Measurement.Mirroring.Tests
{
    public class FrameTripleBufferTests
    {
        [Test]
        public void Size_IsRgba()
        {
            var buffer = new FrameTripleBuffer(4, 2);
            Assert.AreEqual(4 * 2 * 4, buffer.FrameBytes);
            Assert.AreEqual(buffer.FrameBytes, buffer.Back.Length);
        }

        [Test]
        public void NothingPublished_AcquireReturnsFalse()
        {
            var buffer = new FrameTripleBuffer(2, 2);
            Assert.IsFalse(buffer.TryAcquire(out byte[] front));
            Assert.IsNotNull(front);
        }

        [Test]
        public void Publish_ThenAcquire_DeliversThatFrameOnce()
        {
            var buffer = new FrameTripleBuffer(2, 2);
            buffer.Back[0] = 7;
            buffer.Publish();

            Assert.IsTrue(buffer.TryAcquire(out byte[] front));
            Assert.AreEqual(7, front[0]);
            Assert.IsFalse(buffer.TryAcquire(out byte[] again));
            Assert.AreSame(front, again);
        }

        [Test]
        public void SlowConsumer_GetsTheLatestFrame()
        {
            var buffer = new FrameTripleBuffer(2, 2);
            for (byte i = 1; i <= 3; i++)
            {
                buffer.Back[0] = i;
                buffer.Publish();
            }
            Assert.IsTrue(buffer.TryAcquire(out byte[] front));
            Assert.AreEqual(3, front[0]);
        }

        [Test]
        public void ProducerNeverWritesIntoTheBufferTheConsumerHolds()
        {
            var buffer = new FrameTripleBuffer(2, 2);
            buffer.Publish();
            buffer.TryAcquire(out byte[] front);
            for (int i = 0; i < 5; i++)
            {
                Assert.AreNotSame(front, buffer.Back);
                buffer.Publish();
            }
        }

        [Test]
        public void ConcurrentUse_ConsumerAlwaysSeesWholeFrames()
        {
            // Cada cuadro se llena con un mismo valor: si el consumidor viera un buffer a medio escribir, habría mezcla.
            var buffer = new FrameTripleBuffer(64, 64);
            int frames = 0;
            var producer = new Thread(() =>
            {
                for (int n = 1; n <= 2000; n++)
                {
                    byte v = (byte)(n % 250 + 1);
                    byte[] back = buffer.Back;
                    for (int i = 0; i < back.Length; i++) back[i] = v;
                    buffer.Publish();
                }
            });
            producer.Start();
            while (producer.IsAlive || buffer.TryAcquire(out _))
            {
                if (!buffer.TryAcquire(out byte[] front)) continue;
                byte first = front[0];
                foreach (byte b in front) Assert.AreEqual(first, b);
                frames++;
            }
            producer.Join();
            Assert.Greater(frames, 0);
        }

        [Test]
        public void InvalidSize_Throws()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => new FrameTripleBuffer(0, 2));
            Assert.Throws<ArgumentOutOfRangeException>(() => new FrameTripleBuffer(2, -1));
        }
    }
}
