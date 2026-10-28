using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text.RegularExpressions;

namespace IDP.Plugins
{
    public class UpdateIdpEmailSender : IPlugin
    {
        private const string SubjectMatch =
            "Someone subscribed to IDP Americas. Set up an automation to welcome them";

        public void Execute(IServiceProvider serviceProvider)
        {
            var context = (IPluginExecutionContext)serviceProvider.GetService(typeof(IPluginExecutionContext));
            var tracing = (ITracingService)serviceProvider.GetService(typeof(ITracingService));
            var serviceFactory = (IOrganizationServiceFactory)serviceProvider.GetService(typeof(IOrganizationServiceFactory));
            var service = serviceFactory.CreateOrganizationService(context.UserId);

            try
            {
                if (!string.Equals(context.MessageName, "Create", StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }

                if (!context.OutputParameters.Contains("id"))
                {
                    tracing.Trace("Created email id was not available.");
                    return;
                }

                var emailId = (Guid)context.OutputParameters["id"];

                var email = service.Retrieve("email", emailId, new ColumnSet("subject", "description", "from", "statecode", "statuscode"));
                var subject = email.GetAttributeValue<string>("subject");

                var originalState = email.GetAttributeValue<OptionSetValue>("statecode")?.Value ?? 0;
                var originalStatus = email.GetAttributeValue<OptionSetValue>("statuscode")?.Value ?? 1;

                tracing.Trace("Original email State={0}, Status={1}", originalState, originalStatus);

                if (string.IsNullOrWhiteSpace(subject) || subject.IndexOf(SubjectMatch, StringComparison.OrdinalIgnoreCase) < 0)
                {
                    return;
                }

                tracing.Trace("IDP subscriber email detected. Subject: {0}", subject);

                // directioncode = false means incoming.
                //if (email.Attributes.Contains("directioncode"))
                //{
                //    var outgoing = email.GetAttributeValue<bool>("directioncode");

                //    if (outgoing)
                //    {
                //        tracing.Trace("Email is outgoing. Plug-in skipped.");
                //        return;
                //    }
                //}

                var htmlBody = email.GetAttributeValue<string>("description");

                if (string.IsNullOrWhiteSpace(htmlBody))
                {
                    tracing.Trace("Email description/body is empty.");

                    return;
                }

                var subscriber = ParseSubscriber(htmlBody, tracing);

                if (subscriber == null)
                {
                    tracing.Trace("Subscriber information could not be parsed.");
                    return;
                }

                if (string.IsNullOrWhiteSpace(subscriber.Email))
                {
                    tracing.Trace("Subscriber email was not found. Sender not changed.");
                    return;
                }

                var fullName = string.Join(" ", new[]{ subscriber.FirstName, subscriber.LastName }
                                .Where(x => !string.IsNullOrWhiteSpace(x)));

                if (string.IsNullOrWhiteSpace(fullName))
                {
                    fullName = subscriber.Email;
                }

                tracing.Trace("Subscriber parsed: {0} / {1}", fullName, subscriber.Email);

                /*
                 * Replace the sender only on THIS email.
                 *
                 * We deliberately do NOT update the
                 * "Mailchimp Account Services" Contact.
                 */
                var contactId = GetOrCreateContact(service, subscriber, tracing);

                var subscriberParty = new Entity("activityparty");
                subscriberParty["partyid"] = new EntityReference("contact", contactId);
                subscriberParty["addressused"] = subscriber.Email;
                subscriberParty["participationtypemask"] = new OptionSetValue(1);
                var reopened = false;

                try
                {
                    // Temporarily move it back to Open / Draft.
                    if (originalState == 1)
                    {
                        var reopen = new Entity("email", emailId);
                        reopen["statecode"] = new OptionSetValue(0);  // Open
                        reopen["statuscode"] = new OptionSetValue(1); // Draft

                        service.Update(reopen);
                        reopened = true;

                        tracing.Trace("Email temporarily reopened as Draft.");
                    }
                    else if (originalState == 2)
                    {
                        // Don't unexpectedly reopen canceled emails.
                        tracing.Trace("Email is canceled. Sender update skipped.");
                        return;
                    }

                    var update = new Entity("email", emailId);
                    update["from"] = new EntityCollection(new List<Entity> { subscriberParty });

                    service.Update(update);

                    tracing.Trace(
                        "Email From replaced with Contact {0}: {1} <{2}>",
                        contactId,
                        fullName,
                        subscriber.Email
                    );

                    var verifyEmail = service.Retrieve("email", emailId, new ColumnSet("from"));
                    var verifyFrom = verifyEmail.GetAttributeValue<EntityCollection>("from");

                    if (verifyFrom != null)
                    {
                        foreach (var party in verifyFrom.Entities)
                        {
                            var partyId = party.GetAttributeValue<EntityReference>("partyid");

                            tracing.Trace(
                                "VERIFY FROM => Type={0}, Id={1}, Name={2}, Address={3}",
                                partyId?.LogicalName ?? "(null)",
                                partyId?.Id.ToString() ?? "(null)",
                                partyId?.Name ?? "(null)",
                                party.GetAttributeValue<string>("addressused") ?? "(null)"
                            );
                        }
                    }
                }
                finally
                {
                    if (reopened)
                    {
                        var restore = new Entity("email", emailId);
                        restore["statecode"] = new OptionSetValue(originalState);
                        restore["statuscode"] = new OptionSetValue(originalStatus);

                        service.Update(restore);

                        tracing.Trace(
                            "Email restored to original State={0}, Status={1}.",
                            originalState,
                            originalStatus
                        );
                    }
                }
            }
            catch (Exception ex)
            {
                tracing.Trace("UpdateEmailSender failed: {0}", ex);
                return;
            }
        }


        private static SubscriberData ParseSubscriber(string html, ITracingService tracing) {
            var text = HtmlToText(html);

            tracing.Trace("Normalized email body length: {0}", text?.Length ?? 0);

            if (string.IsNullOrWhiteSpace(text))
            {
                return null;
            }

            var lines = text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(x => x.Trim())
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .ToList();

            var result = new SubscriberData
            {
                Email = GetValueAfterLabel(lines, "Email Address"),
                FirstName = GetValueAfterLabel(lines, "First Name"),
                LastName = GetValueAfterLabel(lines,"Last Name"),
                Audience = GetValueAfterLabel(lines,"Audience")
            };

            return result;
        }


        private static string GetValueAfterLabel( IList<string> lines, string label)
        {
            for (var i = 0; i < lines.Count; i++)
            {
                if (!string.Equals(lines[i], label, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                for (var j = i + 1; j < lines.Count; j++)
                {
                    var value = lines[j]?.Trim();

                    if (string.IsNullOrWhiteSpace(value))
                    {
                        continue;
                    }

                    return value;
                }
            }

            return null;
        }


        private static string HtmlToText(string html)
        {
            if (string.IsNullOrWhiteSpace(html))
            {
                return string.Empty;
            }

            var text = html;

            // Turn common HTML structural elements into line breaks.
            text = Regex.Replace(text, @"<br\s*/?>", "\n", RegexOptions.IgnoreCase);
            text = Regex.Replace(text, @"</(td|th|tr|p|div|li|table|h[1-6])\s*>", "\n", RegexOptions.IgnoreCase);

            // Remove style/script content.
            text = Regex.Replace(text, @"<script[\s\S]*?</script>", string.Empty, RegexOptions.IgnoreCase);
            text = Regex.Replace(text, @"<style[\s\S]*?</style>", string.Empty, RegexOptions.IgnoreCase);

            // Remove remaining HTML tags.
            text = Regex.Replace(text, @"<[^>]+>", string.Empty);
            text = WebUtility.HtmlDecode(text);
            text = text.Replace("\u00A0", " ").Replace("\t", " ");

            // Collapse repeated whitespace but retain line boundaries.
            text = Regex.Replace(text, @"[ ]{2,}", " ");
            text = Regex.Replace(text, @"(\r?\n){2,}", "\n");

            return text.Trim();
        }

        private static Guid GetOrCreateContact(IOrganizationService service, SubscriberData subscriber, ITracingService tracing)
        {
            var query = new QueryExpression("contact")
            {
                ColumnSet = new ColumnSet("contactid"),
                TopCount = 1
            };

            query.Criteria.AddCondition("emailaddress1", ConditionOperator.Equal, subscriber.Email);

            var existing = service.RetrieveMultiple(query).Entities.FirstOrDefault();

            if (existing != null)
            {
                tracing.Trace("Existing Contact found: {0}", existing.Id);
                return existing.Id;
            }

            var contact = new Entity("contact")
            {
                ["emailaddress1"] = subscriber.Email
            };

            if (!string.IsNullOrWhiteSpace(subscriber.FirstName))
            {
                contact["firstname"] = subscriber.FirstName;
            }

            if (!string.IsNullOrWhiteSpace(subscriber.LastName))
            {
                contact["lastname"] = subscriber.LastName;
            }

            var contactId = service.Create(contact);
            tracing.Trace("New Contact created: {0}", contactId);

            return contactId;
        }

        private class SubscriberData
        {
            public string FirstName { get; set; }

            public string LastName { get; set; }

            public string Email { get; set; }

            public string Audience { get; set; }
        }
    }
}