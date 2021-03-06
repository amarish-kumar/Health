﻿// <copyright file="SlackIncomingWebHookHealthAlerter.cs" company="App Metrics Contributors">
// Copyright (c) App Metrics Contributors. All rights reserved.
// </copyright>

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using App.Metrics.Health.Reporting.Slack.Internal;

namespace App.Metrics.Health.Reporting.Slack
{
    public class SlackIncomingWebHookHealthAlerter : IReportHealthStatus
    {
        private const string DefaultEmojiIcon = ":broken_heart:";
        private const string DegradingColor = "#FFCC00";
        private const string FailingColor = "#F35A00";
        private const string RecoveredEmojiIcon = ":green_heart:";
        private const string SuccessColor = "#5CB85C";
        private static readonly DateTime Epoch = new DateTime(1970, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc);
        private static readonly HashSet<string> LastFailedCheckCache = new HashSet<string>();
        private readonly HttpClient _httpClient;
        private readonly SlackHealthAlertOptions _slackOptions;

        public SlackIncomingWebHookHealthAlerter(SlackHealthAlertOptions slackOptions)
        {
            _slackOptions = slackOptions ?? throw new ArgumentNullException(nameof(slackOptions));
            _httpClient = new HttpClient();
        }

        /// <inheritdoc />
        public async Task ReportAsync(HealthOptions options, HealthStatus status, CancellationToken cancellationToken = default)
        {
            var applicationName = options.ApplicationName;

            if (Uri.TryCreate(applicationName, UriKind.Absolute, out var appUri))
            {
                applicationName = $"<{appUri}|{appUri}>";
            }

            var slackMessage = new SlackPayload
                               {
                                   Text = $"*{applicationName} Unhealthy*",
                                   Channel = _slackOptions.Channel,
                                   UserName = _slackOptions.Username,
                                   IconEmoji = string.IsNullOrWhiteSpace(_slackOptions.EmojiIcon) ? DefaultEmojiIcon : _slackOptions.EmojiIcon
                               };

            var unhealthyCheck = status.Results.Where(r => r.Check.Status == HealthCheckStatus.Unhealthy).ToList();

            if (unhealthyCheck.Any())
            {
                AddHealthAttachments(HealthCheckStatus.Unhealthy, unhealthyCheck, slackMessage);
            }

            if (_slackOptions.AlertOnDegradedChecks)
            {
                var degradedChecks = status.Results.Where(r => r.Check.Status == HealthCheckStatus.Degraded).ToList();

                if (degradedChecks.Any())
                {
                    AddHealthAttachments(HealthCheckStatus.Degraded, degradedChecks, slackMessage);
                }
            }

            if (slackMessage.Attachments.Any())
            {
                await _httpClient.PostAsync(_slackOptions.WebhookUrl, new JsonContent(slackMessage), cancellationToken);
            }

            await AlertRecoveredChecks(status, applicationName, cancellationToken);
        }

        private void AddHealthAttachments(
            HealthCheckStatus checkStatus,
            IReadOnlyCollection<HealthCheck.Result> checks,
            SlackPayload slackMessage)
        {
            var checkOrChecks = checks.Count > 1 ? "checks" : "check";
            var textEnd = string.Empty;
            var color = string.Empty;

            if (checkStatus == HealthCheckStatus.Unhealthy)
            {
                textEnd = "failing";
                color = FailingColor;
            }

            if (checkStatus == HealthCheckStatus.Degraded)
            {
                textEnd = "degrading";
                color = DegradingColor;
            }

            if (checkStatus == HealthCheckStatus.Healthy)
            {
                textEnd = "recovered";
                color = SuccessColor;
            }

            var attachment = new SlackAttachment
                             {
                                 Text = $"*_{checks.Count} {checkOrChecks} {textEnd}_*",
                                 Color = color,
                                 Ts = (DateTime.UtcNow - Epoch).TotalSeconds
                             };

            foreach (var result in checks)
            {
                if (result.Check.Status != HealthCheckStatus.Healthy)
                {
                    LastFailedCheckCache.Add(result.Name);
                }

                attachment.Fields.Add(
                    new SlackAttachmentFields
                    {
                        Title = $">{result.Name}",
                        Value = $">_{result.Check.Message}_",
                        Short = true
                    });
            }

            slackMessage.Attachments.Add(attachment);
        }

        private async Task AlertRecoveredChecks(HealthStatus status, string applicationName, CancellationToken cancellationToken)
        {
            var healthyChecks = status.Results.Where(r => r.Check.Status == HealthCheckStatus.Healthy).ToList();

            if (!healthyChecks.Any())
            {
                return;
            }

            var recoveredChecks = new List<HealthCheck.Result>();

            foreach (var check in healthyChecks)
            {
                if (LastFailedCheckCache.Contains(check.Name))
                {
                    recoveredChecks.Add(check);
                    LastFailedCheckCache.Remove(check.Name);
                }
            }

            if (recoveredChecks.Any())
            {
                var slackMessage = new SlackPayload
                                   {
                                       Text = $"*{applicationName} Health Recovered*",
                                       Channel = _slackOptions.Channel,
                                       UserName = _slackOptions.Username,
                                       IconEmoji = string.IsNullOrWhiteSpace(_slackOptions.EmojiIcon) ? RecoveredEmojiIcon : _slackOptions.EmojiIcon
                                   };

                AddHealthAttachments(HealthCheckStatus.Healthy, recoveredChecks, slackMessage);

                if (slackMessage.Attachments.Any())
                {
                    await _httpClient.PostAsync(_slackOptions.WebhookUrl, new JsonContent(slackMessage), cancellationToken);
                }
            }
        }
    }
}