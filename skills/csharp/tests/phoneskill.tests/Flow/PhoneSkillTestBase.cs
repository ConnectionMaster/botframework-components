﻿// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Security.Claims;
using System.Text;
using System.Threading;
using Microsoft.Bot.Builder;
using Microsoft.Bot.Builder.Adapters;
using Microsoft.Bot.Builder.AI.Luis;
using Microsoft.Bot.Builder.LanguageGeneration;
using Microsoft.Bot.Connector;
using Microsoft.Bot.Connector.Authentication;
using Microsoft.Bot.Schema;
using Microsoft.Bot.Solutions;
using Microsoft.Bot.Solutions.Authentication;
using Microsoft.Bot.Solutions.Proactive;
using Microsoft.Bot.Solutions.Responses;
using Microsoft.Bot.Solutions.TaskExtensions;
using Microsoft.Bot.Solutions.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PhoneSkill.Bots;
using PhoneSkill.Common;
using PhoneSkill.Dialogs;
using PhoneSkill.Models;
using PhoneSkill.Models.Actions;
using PhoneSkill.Services;
using PhoneSkill.Tests.TestDouble;
using PhoneSkill.Utilities;

namespace PhoneSkill.Tests.Flow
{
    public class PhoneSkillTestBase
    {
        public static readonly string Provider = "Azure Active Directory v2";

        public IServiceCollection Services { get; set; }

        public ConversationState ConversationState { get; set; }

        public UserState UserState { get; set; }

        public ProactiveState ProactiveState { get; set; }

        public IBotTelemetryClient TelemetryClient { get; set; }

        public IBackgroundTaskQueue BackgroundTaskQueue { get; set; }

        public IServiceManager ServiceManager { get; set; }

        public LocaleTemplateManager TemplateManager { get; set; }

        public Templates Templates { get; set; }

        [TestInitialize]
        public void Initialize()
        {
            // Initialize mock service manager
            ServiceManager = new FakeServiceManager();

            // Initialize service collection
            Services = new ServiceCollection();
            Services.AddSingleton(new BotSettings()
            {
                OAuthConnections = new List<OAuthConnection>()
                {
                    new OAuthConnection() { Name = Provider, Provider = Provider }
                }
            });

            Services.AddSingleton(new BotServices()
            {
                CognitiveModelSets = new Dictionary<string, CognitiveModelSet>
                {
                    {
                        "en-us", new CognitiveModelSet()
                        {
                            LuisServices = new Dictionary<string, LuisRecognizer>
                            {
                                { "general", PhoneSkillMockLuisRecognizerFactory.CreateMockGeneralLuisRecognizer() },
                                { "phone", PhoneSkillMockLuisRecognizerFactory.CreateMockPhoneLuisRecognizer() },
                                { "contactSelection", PhoneSkillMockLuisRecognizerFactory.CreateMockContactSelectionLuisRecognizer() },
                                { "phoneNumberSelection", PhoneSkillMockLuisRecognizerFactory.CreateMockPhoneNumberSelectionLuisRecognizer() },
                            }
                        }
                    }
                }
            });

            Services.AddSingleton<IBotTelemetryClient, NullBotTelemetryClient>();
            Services.AddSingleton(new UserState(new MemoryStorage()));
            Services.AddSingleton(new ConversationState(new MemoryStorage()));
            Services.AddSingleton(new ProactiveState(new MemoryStorage()));
            Services.AddSingleton(sp =>
            {
                var userState = sp.GetService<UserState>();
                var conversationState = sp.GetService<ConversationState>();
                var proactiveState = sp.GetService<ProactiveState>();
                return new BotStateSet(userState, conversationState);
            });

            // Configure localized responses
            TemplateManager = LocaleTemplateManagerWrapper.CreateLocaleTemplateManager("en-us");
            Services.AddSingleton(TemplateManager);
            Templates = LocaleTemplateManagerWrapper.CreateTemplates();

            Services.AddSingleton<IBackgroundTaskQueue, BackgroundTaskQueue>();
            Services.AddSingleton<IServiceManager>(ServiceManager);
            Services.AddSingleton<TestAdapter, DefaultTestAdapter>();
            Services.AddTransient<MainDialog>();
            Services.AddTransient<OutgoingCallDialog>();
            Services.AddTransient<IBot, DefaultActivityHandler<MainDialog>>();
        }

        public TestFlow GetTestFlow()
        {
            var sp = Services.BuildServiceProvider();
            var adapter = sp.GetService<TestAdapter>();
            adapter.AddUserToken(Provider, Channels.Test, adapter.Conversation.User.Id, "test");

            var conversationState = sp.GetService<ConversationState>();
            var stateAccessor = conversationState.CreateProperty<PhoneSkillState>(nameof(PhoneSkillState));

            var testFlow = new TestFlow(adapter, async (context, token) =>
            {
                var bot = sp.GetService<IBot>();
                var state = await stateAccessor.GetAsync(context, () => new PhoneSkillState());
                state.SourceOfContacts = ContactSource.Microsoft;
                await bot.OnTurnAsync(context, CancellationToken.None);
            });

            return testFlow;
        }

        protected TestFlow GetSkillTestFlow()
        {
            var sp = Services.BuildServiceProvider();
            var adapter = sp.GetService<TestAdapter>();
            adapter.AddUserToken(Provider, Channels.Test, adapter.Conversation.User.Id, "test");

            var testFlow = new TestFlow(adapter, async (context, token) =>
            {
                // Set claims in turn state to simulate skill mode
                var claims = new List<Claim>();
                claims.Add(new Claim(AuthenticationConstants.VersionClaim, "1.0"));
                claims.Add(new Claim(AuthenticationConstants.AudienceClaim, Guid.NewGuid().ToString()));
                claims.Add(new Claim(AuthenticationConstants.AppIdClaim, Guid.NewGuid().ToString()));
                context.TurnState.Add("BotIdentity", new ClaimsIdentity(claims));

                var bot = sp.GetService<IBot>();
                await bot.OnTurnAsync(context, CancellationToken.None);
            });

            return testFlow;
        }

        protected Action<IActivity> Message(string templateId, Dictionary<string, object> tokens = null, IList<string> selectionItems = null)
        {
            return activity =>
            {
                Assert.AreEqual("message", activity.Type);
                var messageActivity = activity.AsMessageActivity();

                // Work around a bug in ParseReplies.
                if (tokens == null)
                {
                    tokens = new Dictionary<string, object>();
                }

                var expectedTexts = ParseReplies(templateId, tokens);

                if (selectionItems != null)
                {
                    var selectionListBuilder = new StringBuilder("\n");
                    for (var i = 0; i < selectionItems.Count; ++i)
                    {
                        selectionListBuilder.Append("\n   ");
                        selectionListBuilder.Append(i + 1);
                        selectionListBuilder.Append(". ");
                        selectionListBuilder.Append(selectionItems[i]);
                    }

                    var selectionListString = selectionListBuilder.ToString();

                    var newExpectedTexts = new string[expectedTexts.Length];
                    for (int i = 0; i < expectedTexts.Length; ++i)
                    {
                        newExpectedTexts[i] = expectedTexts[i] + selectionListString;
                    }

                    expectedTexts = newExpectedTexts;
                }

                var actualText = messageActivity.Text;
                Assert.IsNotNull(actualText, $"Text of message activity was null. Expected one of: {expectedTexts.ToPrettyString()}\n");

                string bestMatchingExpectedText = string.Empty;
                int longestCommonPrefix = 0;
                foreach (var expectedText in expectedTexts)
                {
                    for (int i = 0; i < expectedText.Length && i < actualText.Length; ++i)
                    {
                        if (expectedText[i] != actualText[i])
                        {
                            if (i > longestCommonPrefix)
                            {
                                bestMatchingExpectedText = expectedText;
                                longestCommonPrefix = i;
                            }

                            break;
                        }
                    }
                }

                CollectionAssert.Contains(expectedTexts, actualText,
                    $"Expected one of: {expectedTexts.ToPrettyString()}\nActual: {actualText}\nBest matching expected text matches up to {longestCommonPrefix}: {bestMatchingExpectedText.Substring(0, longestCommonPrefix)}\n");

                foreach (string substring in tokens.Values)
                {
                    StringAssert.Contains(actualText, substring, $"Expected string that contains \"{substring}\"\nActual: {actualText}\n");
                }
            };
        }

        protected Action<IActivity> OutgoingCallEvent(OutgoingCall expectedCall)
        {
            return activity =>
            {
                Assert.AreEqual("event", activity.Type);
                var eventReceived = activity.AsEventActivity();
                Assert.AreEqual("PhoneSkill.OutgoingCall", eventReceived.Name);
                Assert.IsInstanceOfType(eventReceived.Value, typeof(OutgoingCall));
                Assert.AreEqual(expectedCall, (OutgoingCall)eventReceived.Value);
            };
        }

        protected Action<IActivity> SkillActionEndMessage(bool? success = null)
        {
            return activity =>
            {
                Assert.AreEqual(activity.Type, ActivityTypes.EndOfConversation);
                if (success.HasValue)
                {
                    var result = ((Activity)activity).Value as ActionResult;
                    Assert.AreEqual(result.ActionSuccess, success.Value);
                }
                else
                {
                    Assert.IsNull(((Activity)activity).Value);
                }
            };
        }

        protected string[] ParseReplies(string name, Dictionary<string, object> data = null)
        {
            return Templates.ParseReplies(name, data);
        }
    }
}
