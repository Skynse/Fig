using System;
namespace Fig.Core.Timeline
{
    public class PlayheadController
    {
        public double PositionSec { get; set; }

        public bool IsPlaying { get; private set; }

        public void Play()
        {
            throw new NotImplementedException();
        }

        public void Pause()
        {
            throw new NotImplementedException();
        }

        public void Seek(double sec)
        {
            throw new NotImplementedException();
        }

        public void Tick(double elapsedSec)
        {
            throw new NotImplementedException();
        }
    }
}
