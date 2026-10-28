using Microsoft.Xrm.Sdk;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace IDP.Plugins
{
    public class TraceEmailChange : IPlugin
    {
        public void Execute(IServiceProvider serviceProvider)
        {
            var context = (IPluginExecutionContext)serviceProvider.GetService(typeof(IPluginExecutionContext));
            var tracing = (ITracingService)serviceProvider.GetService(typeof(ITracingService));

            if (!string.Equals(context.MessageName, "Update", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            if (!(context.InputParameters.TryGetValue("Target", out var value) && value is Entity target))
            {
                return;
            }

            if (target.LogicalName != "email" || !target.Contains("from"))
            {
                return;
            }

            TraceContext(context, tracing);

            tracing.Trace(
                "EXTRA CONTEXT => IsInTransaction={0}, IsolationMode={1}, " +
                "OperationCreatedOn={2:o}, BusinessUnitId={3}, OrganizationId={4}",
                context.IsInTransaction,
                context.IsolationMode,
                context.OperationCreatedOn,
                context.BusinessUnitId,
                context.OrganizationId
            );

            tracing.Trace("TARGET ATTRIBUTES:");

            foreach (var attribute in target.Attributes)
            {
                tracing.Trace(
                    "  {0} = {1}",
                    attribute.Key,
                    FormatValue(attribute.Value)
                );
            }

            var from = target.GetAttributeValue<EntityCollection>("from");

            if (from == null || from.Entities.Count == 0)
            {
                tracing.Trace("FROM is empty.");
                return;
            }

            foreach (var party in from.Entities)
            {
                var partyId = party.GetAttributeValue<EntityReference>("partyid");

                tracing.Trace(
                    "NEW FROM => Type={0}, Id={1}, Name={2}, Address={3}, ParticipationType={4}",
                    partyId?.LogicalName ?? "(null)",
                    partyId?.Id.ToString() ?? "(null)",
                    partyId?.Name ?? "(null)",
                    party.GetAttributeValue<string>("addressused") ?? "(null)",
                    party.GetAttributeValue<OptionSetValue>("participationtypemask")?.Value.ToString() ?? "(null)"
                );
            }
        }

        private static void TraceContext(IPluginExecutionContext ctx, ITracingService tracing, string prefix = "CTX")
        {
            if (ctx == null)
                return;

            tracing.Trace(
                "{0} => Message={1}, Stage={2}, Mode={3}, Depth={4}, " +
                "PrimaryEntity={5}, PrimaryId={6}, UserId={7}, InitiatingUserId={8}, " +
                "CorrelationId={9}, RequestId={10}, OperationId={11}, " +
                "OwningExtensionType={12}, OwningExtensionId={13}, OwningExtensionName={14}",
                prefix,
                ctx.MessageName,
                ctx.Stage,
                ctx.Mode,
                ctx.Depth,
                ctx.PrimaryEntityName,
                ctx.PrimaryEntityId,
                ctx.UserId,
                ctx.InitiatingUserId,
                ctx.CorrelationId,
                ctx.RequestId,
                ctx.OperationId,
                ctx.OwningExtension?.LogicalName ?? "(null)",
                ctx.OwningExtension?.Id.ToString() ?? "(null)",
                ctx.OwningExtension?.Name ?? "(null)"
            );

            if (ctx.ParentContext != null)
                TraceContext(ctx.ParentContext, tracing, prefix + ".PARENT");
        }

        private static string FormatValue(object value)
        {
            if (value == null)
                return "(null)";

            if (value is EntityReference er)
                return $"{er.LogicalName}:{er.Id} ({er.Name})";

            if (value is OptionSetValue osv)
                return osv.Value.ToString();

            if (value is Money money)
                return money.Value.ToString();

            if (value is EntityCollection ec)
            {
                var values = ec.Entities.Select(e =>
                {
                    var party = e.GetAttributeValue<EntityReference>("partyid");
                    var address = e.GetAttributeValue<string>("addressused");

                    return $"[{party?.LogicalName}:{party?.Id}, Name={party?.Name}, Address={address}]";
                });

                return string.Join(", ", values);
            }

            return value.ToString();
        }
    }
}
