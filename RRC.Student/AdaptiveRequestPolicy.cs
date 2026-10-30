using System;
using System.Collections.Generic;
using System.Linq;
using RRC.Abstractions;

namespace RRC.Student;

public sealed class AdaptiveRequestPolicy : IRequestPolicy
{
    // Скользящее окно для хранения времени успешных запросов
    private readonly Queue<TimeSpan> _recentLatencies = new();
    private const int MaxHistory = 100;
    private const int MaxAttempts = 3;

    public TimeSpan GetTimeout(RequestContext context, Random random)
    {
        // Если истории нет, берем безопасный таймаут по умолчанию.
        // В публичном сценарии latency до 150мс + нагрузка. 250мс - хороший старт.
        if (_recentLatencies.Count == 0)
        {
            return TimeSpan.FromMilliseconds(250);
        }

        // Считаем 95-й процентиль последних успешных запросов
        var sorted = _recentLatencies.OrderBy(x => x).ToArray();
        int index = (int)Math.Ceiling(0.95 * sorted.Length) - 1;
        index = Math.Clamp(index, 0, sorted.Length - 1);
        var p95 = sorted[index];

        // Добавляем запас 50% на случай скачков нагрузки (in-flight > capacity)
        var timeoutMs = p95.TotalMilliseconds * 1.5;

        // Ограничиваем таймаут разумными рамками (от 150 до 1500 мс)
        timeoutMs = Math.Clamp(timeoutMs, 150, 1500);

        // Небольшой рандомный разброс, чтобы запросы не шли "пачкой" одновременно
        timeoutMs += random.Next(0, 25);

        return TimeSpan.FromMilliseconds(timeoutMs);
    }

    public RetryDecision OnResult(RequestObservation observation, Random random)
    {
        if (observation.Outcome == RequestOutcome.Success)
        {
            // ВАЖНО: Проверьте название свойства в классе RequestObservation.cs!
            // Если Visual Studio подчеркивает observation.Elapsed красным,
            // замените Elapsed на то, что там написано (возможно Duration или ElapsedTime).
            _recentLatencies.Enqueue(observation.Elapsed);

            if (_recentLatencies.Count > MaxHistory)
            {
                _recentLatencies.Dequeue();
            }

            return RetryDecision.NoRetry();
        }

        // Обработка таймаута
        if (observation.Attempt >= MaxAttempts)
        {
            // Сдаемся после 3-х попыток, чтобы не забивать сервер
            return RetryDecision.NoRetry();
        }

        // Экспоненциальная задержка перед повтором + случайный джиттер (jitter).
        // 1-я неудача: ~150 мс + рандом, 2-я неудача: ~300 мс + рандом.
        // Это дает серверу время "разгрести" очередь и завершить опоздавшие запросы.
        double baseDelayMs = 150 * Math.Pow(2, observation.Attempt - 1);
        double jitter = random.NextDouble() * 50;
        double delayMs = baseDelayMs + jitter;

        return RetryDecision.RetryAfter(TimeSpan.FromMilliseconds(delayMs));
    }
}