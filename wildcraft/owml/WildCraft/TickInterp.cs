using System;
using System.Collections.Generic;
using WildCraft;

namespace Tecno.WildCraft;

/// <summary>
/// Minecraft runs physics at 20 Hz; Outer Wilds renders at its own rate. Like SkyCraft's Game.cpp, keep a short
/// history of Minecraft's ticks and render slightly in the past, pure interpolation (never extrapolation), so
/// the player glides instead of stepping, whatever either game's frame rate does.
/// </summary>
public sealed class TickInterp
{
	struct Tick
	{
		public HostLink.McSnapshot S;
		public long At;
		public int Slots;
	}

	readonly List<Tick> _hist = new List<Tick>();
	long _lastFrame;
	int _outliers;
	double _delayMs = 12.0;
	readonly double[] _due = new double[40];
	int _dueNext;
	bool _dueInit;

	public bool Valid => _hist.Count > 0;

	public void Reset()
	{
		_hist.Clear();
		_lastFrame = 0;
		_outliers = 0;
		_dueInit = false;
		_delayMs = 12.0;
	}

	public struct Pose
	{
		public double FeetX, FeetY, FeetZ;
		public float EyeHeight;
		public float BobPhase, BobAmount;
	}

	/// <summary>Takes the newest McState and evaluates the player's pose for "now".</summary>
	public Pose Evaluate(in HostLink.McSnapshot mc, long now)
	{
		var pose = new Pose
		{
			FeetX = mc.X, FeetY = mc.Y, FeetZ = mc.Z,
			EyeHeight = (float)(mc.EyeY - mc.Y),
			BobPhase = mc.BobPhase, BobAmount = mc.BobAmount,
		};
		if (mc.TickQpc == 0 || mc.TickMs <= 0f) return pose;

		double qpcPerMs = HostLink.QpcFrequency / 1000.0;
		long period = Math.Max(1, (long)Math.Round(mc.TickMs * qpcPerMs));

		if (_hist.Count == 0 || _hist[_hist.Count - 1].S.TickQpc != mc.TickQpc)
		{
			if (_hist.Count > 0 && mc.TickQpc < _hist[_hist.Count - 1].S.TickQpc) _hist.Clear();   // Minecraft restarted
			var tick = new Tick { S = mc, At = mc.TickQpc, Slots = 1 };
			if (_hist.Count > 0)
			{
				var last = _hist[_hist.Count - 1];
				long n = (long)Math.Round((double)(mc.TickQpc - last.At) / period);
				long err = mc.TickQpc - (last.At + n * period);
				if (n == 0 && last.Slots >= 2)
				{
					last.At -= period; last.Slots -= 1;
					_hist[_hist.Count - 1] = last;
					tick.At = last.At + period;
				}
				else if (n >= 1 && n <= 10 && Math.Abs(err) < period * 3 / 10)
				{
					tick.At = last.At + n * period + err / 16;
					tick.Slots = (int)n;
					_outliers = 0;
				}
				else if (n <= 10 && ++_outliers < 3)
				{
					tick.Slots = (int)Math.Max(n, 1);
					tick.At = last.At + tick.Slots * period;
				}
				else _outliers = 0;
			}
			if (_lastFrame != 0)
			{
				if (!_dueInit) { for (int i = 0; i < _due.Length; i++) _due[i] = _delayMs - 1.0; _dueInit = true; }
				double dueMs = (double)(_lastFrame - tick.At) / qpcPerMs;
				if (dueMs < 30.0) _due[_dueNext++ % _due.Length] = dueMs;
			}
			_hist.Add(tick);
			if (_hist.Count > 8) _hist.RemoveAt(0);
		}

		double frameMs = _lastFrame != 0 ? (double)(now - _lastFrame) / qpcPerMs : 0.0;
		_lastFrame = now;
		if (_dueInit)
		{
			double max = double.MinValue;
			foreach (var d in _due) if (d > max) max = d;
			double target = Math.Max(4.0, Math.Min(30.0, max + 1.0));
			double dt = Math.Min(frameMs, 100.0) / 1000.0;
			_delayMs = target > _delayMs ? Math.Min(target, _delayMs + 20.0 * dt) : Math.Max(target, _delayMs - 2.0 * dt);
		}
		long renderQpc = now - (long)Math.Round(_delayMs * qpcPerMs);

		int idx = 0;
		for (int k = _hist.Count; k-- > 0;)
			if (_hist[k].At <= renderQpc) { idx = k; break; }
		var t0 = _hist[idx];
		bool hasNext = idx + 1 < _hist.Count;
		double ticks = (double)(renderQpc - t0.At) / period;
		double t = Math.Max(0.0, Math.Min(1.0, ticks));
		var s = t0.S;
		pose.FeetX = s.PrevX + (s.CurX - s.PrevX) * t;
		pose.FeetY = s.PrevY + (s.CurY - s.PrevY) * t;
		pose.FeetZ = s.PrevZ + (s.CurZ - s.PrevZ) * t;
		pose.EyeHeight = (float)(s.TickEyeO + (s.TickEye - s.TickEyeO) * t);
		pose.BobPhase = -(s.WalkDist + (s.WalkDist - s.WalkDistO) * (float)t);
		pose.BobAmount = s.BobO + (s.Bob - s.BobO) * (float)t;
		if (ticks > 1.0 && hasNext)
		{
			var n = _hist[idx + 1];
			double gap = n.At - (t0.At + period);
			double u = gap > 0.0 ? Math.Max(0.0, Math.Min(1.0, (renderQpc - (t0.At + period)) / gap)) : 1.0;
			pose.FeetX = s.CurX + (n.S.PrevX - s.CurX) * u;
			pose.FeetY = s.CurY + (n.S.PrevY - s.CurY) * u;
			pose.FeetZ = s.CurZ + (n.S.PrevZ - s.CurZ) * u;
			pose.EyeHeight = (float)(s.TickEye + (n.S.TickEyeO - s.TickEye) * u);
			float endPhase = -(s.WalkDist + (s.WalkDist - s.WalkDistO));
			pose.BobPhase = endPhase + (-n.S.WalkDist - endPhase) * (float)u;
			pose.BobAmount = s.Bob + (n.S.BobO - s.Bob) * (float)u;
		}
		return pose;
	}
}
