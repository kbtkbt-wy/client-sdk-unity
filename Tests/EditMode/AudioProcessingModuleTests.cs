using NUnit.Framework;

namespace LiveKit.EditModeTests
{
    public class AudioProcessingModuleTests
    {
        [Test]
        public void TenMsFrameSampleCount_MatchesExpectedValues()
        {
            Assert.AreEqual(960, AudioProcessingModule.GetTenMsFrameSampleCount(48000, 2));
            Assert.AreEqual(160, AudioProcessingModule.GetTenMsFrameSampleCount(16000, 1));
        }

        [Test]
        public void Upsample24kTo48k_InterpolatesMonoData()
        {
            var input = new short[] { 0, 1000, 2000 };
            var output = AudioProcessingModule.Upsample24kTo48k(input, 1);

            CollectionAssert.AreEqual(new short[] { 0, 500, 1000, 1500, 2000, 2000 }, output);
        }

        [Test]
        public void Downsample48kTo24k_AveragesPairs()
        {
            var input = new short[] { 0, 100, 200, 300, 400, 500 };
            var output = new short[3];

            AudioProcessingModule.Downsample48kTo24k(input, output, 1);

            CollectionAssert.AreEqual(new short[] { 50, 250, 450 }, output);
        }
    }
}
