using System;
using System.Collections.Generic;
using System.Linq;
using RRC.Abstractions;

namespace RRC.Student;

public sealed class ReqAdoptUtkevich : IRequestPolicy
{
    
    private readonly Queue<TimeSpan> _recentLatencies = new();
    private const int MaxHistory = 100;
    private const int MaxAttempts = 3;

    public TimeSpan GetTimeout(RequestContext context, Random random)
    {
        
        if (_recentLatencies.Count == 0)
        {
            return TimeSpan.FromMilliseconds(250);
        }

       
        var sorted = _recentLatencies.OrderBy(x => x).ToArray();
        int index = (int)Math.Ceiling(0.95 * sorted.Length) - 1;
        index = Math.Clamp(index, 0, sorted.Length - 1);
        var p95 = sorted[index];

        
        var timeoutMs = p95.TotalMilliseconds * 1.5;

      
        timeoutMs = Math.Clamp(timeoutMs, 150, 2500);

       
        timeoutMs += random.Next(0, 25);

        return TimeSpan.FromMilliseconds(timeoutMs);
    }

    public RetryDecision OnResult(RequestObservation observation, Random random)
    {
        _recentLatencies.Enqueue(observation.Elapsed);

        if (_recentLatencies.Count > MaxHistory)
        {
            _recentLatencies.Dequeue();
        }

        if (observation.Outcome == RequestOutcome.Success)
        {
            return RetryDecision.NoRetry(); // Успех, повтор не нужен
        }

        // Обработка таймаута
        if (observation.Attempt >= MaxAttempts)
        {
            return RetryDecision.NoRetry();
        }

        // Экспоненциальная задержка перед повтором
        double baseDelayMs = 150 * Math.Pow(2, observation.Attempt - 1);
        double jitter = random.NextDouble() * 50;
        double delayMs = baseDelayMs + jitter;

        return RetryDecision.RetryAfter(TimeSpan.FromMilliseconds(delayMs));
    }
}