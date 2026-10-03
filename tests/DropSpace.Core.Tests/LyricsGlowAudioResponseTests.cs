using DropSpace.Core.Lyrics;
namespace DropSpace.Core.Tests;

[TestClass]
public sealed class LyricsGlowAudioResponseTests
{
    [TestMethod]
    [DataRow(.4)]
    [DataRow(.82)]
    public void BothQuietAndLoudPassagesRetainTransientHeadroom(double level)
    {
        var response = new LyricsGlowAudioResponse();
        for(var i=0;i<180;i++)response.Advance(level,[level,0,level,0,0,0],TimeSpan.FromSeconds(1d/30));
        var steady=response.Energy;
        Assert.IsTrue(steady<.6);
        response.Advance(level+.08,[level+.08,0,level,0,0,0],TimeSpan.FromSeconds(1d/30));
        Assert.IsGreaterThan(.3,response.Energy-steady);
        Assert.IsGreaterThan(response.Bands[2],response.Bands[0]);
        Assert.AreEqual(0d,response.Bands[1]);
    }

    [TestMethod]
    public void ConstantAudioDoesNotInventPulsesAndSilenceNeverAmplifies()
    {
        var response=new LyricsGlowAudioResponse();
        response.Advance(.9,[.9,.9,.9,.9,.9,.9],TimeSpan.FromSeconds(.033));
        var value=response.Energy;
        for(var i=0;i<500;i++){response.Advance(.9,[.9,.9,.9,.9,.9,.9],TimeSpan.FromSeconds(.033));Assert.AreEqual(value,response.Energy,1e-9);}
        response.Advance(0,null,TimeSpan.FromSeconds(.033));
        Assert.AreEqual(0d,response.Energy);Assert.IsTrue(response.Bands.All(x=>x==0));
    }

    [TestMethod]
    public void ResetDiscardsPreviousSongReferenceAndInvalidInputsAreBounded()
    {
        var response=new LyricsGlowAudioResponse();
        response.Advance(.95,[1,1,1,1,1,1],TimeSpan.FromSeconds(.033));response.Reset();
        response.Advance(.2,[.2,double.NaN,double.PositiveInfinity,-1,2],TimeSpan.FromSeconds(.033));
        Assert.AreEqual(.11,response.Energy,1e-9);
        Assert.IsTrue(response.Bands.All(x=>double.IsFinite(x)&&x>=0&&x<=1));
        Assert.AreEqual(0d,response.Bands[1]);Assert.AreEqual(0d,response.Bands[2]);Assert.AreEqual(0d,response.Bands[3]);
    }

    [TestMethod]
    public void CompressedMusicHasLargerVisibleRangeWithoutRaisingMaximumBrightness()
    {
        var old=new LyricsGlowEnvelope();var updated=new LyricsGlowEnvelope();var response=new LyricsGlowAudioResponse();
        var a=new List<double>();var b=new List<double>();
        for(var i=0;i<300;i++)
        {
            var level=.8+.045*Math.Sin(i*.35);double[] bands=[level,level,level,level,level,level];var dt=TimeSpan.FromSeconds(1d/30);
            old.Advance(true,level,dt,bands:bands);response.Advance(level,bands,dt);updated.Advance(true,response.Energy,dt,bands:response.Bands);
            if(i>90){a.Add(old.Brightness);b.Add(updated.Brightness);}
        }
        Assert.IsGreaterThan(3*(a.Max()-a.Min()),b.Max()-b.Min());
        Assert.IsTrue(b.Max()<=.46);
    }
}
