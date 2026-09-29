namespace Centerix.Domain.Platform.Contracts.EligibilityRules;

using System.Text.Json;
using System.Text.Json.Nodes;
using Centerix.Domain.Platform.Promotions.Enums;

/// <summary>
/// Deterministic JSON serializer/deserializer for <see cref="EligibilityRule"/>.
/// </summary>
/// <remarks>
/// Design constraints:
/// - Type discriminator is a string constant ("all_of", "contract_active", etc.) — never a CLR type name.
/// - All properties are written with explicit keys — never relies on reflection ordering.
/// - Deterministic: same rule tree always produces the same JSON string.
/// - Deserialization rejects unknown type discriminators.
/// - No executable code, no C# expressions, no TypeNameHandling.
///
/// JSON format:
/// <code>
/// {
///   "type": "all_of",
///   "rules": [
///     { "type": "contract_active" },
///     { "type": "payment_terms_eq", "paymentTerms": "FullUpfront" },
///     { "type": "amount_paid_at_least", "amount": 5000.00 }
///   ]
/// }
/// </code>
/// </remarks>
public static class EligibilityRuleSerializer
{
    // ─── Type discriminator constants ─────────────────────────────────────────

    private const string TypeKey = "type";
    private const string AllOf = "all_of";
    private const string AnyOf = "any_of";
    private const string ContractActive = "contract_active";
    private const string PaymentTermsEq = "payment_terms_eq";
    private const string PaymentMethodEq = "payment_method_eq";
    private const string CompletedByUtcType = "completed_by_utc";
    private const string NoOverdueInstallment = "no_overdue_installment";
    private const string AmountPaidAtLeast = "amount_paid_at_least";
    private const string DaysFromContractStartGte = "days_from_contract_start_gte";
    private const string DurationMonthsGte = "duration_months_gte";

    private static readonly JsonSerializerOptions _options = new()
    {
        WriteIndented = false
    };

    // ─── Public API ───────────────────────────────────────────────────────────

    /// <summary>
    /// Serializes an <see cref="EligibilityRule"/> to a deterministic JSON string.
    /// </summary>
    /// <param name="rule">The rule to serialize. Must not be null.</param>
    /// <returns>A non-null JSON string representation.</returns>
    public static string Serialize(EligibilityRule rule)
    {
        if (rule is null) throw new ArgumentNullException(nameof(rule));
        var node = BuildNode(rule);
        return node.ToJsonString(_options);
    }

    /// <summary>
    /// Deserializes an <see cref="EligibilityRule"/> from a JSON string.
    /// </summary>
    /// <param name="json">A JSON string previously produced by <see cref="Serialize"/>.</param>
    /// <returns>The reconstructed rule.</returns>
    /// <exception cref="ArgumentException">Thrown when the JSON is null, empty, malformed, or contains an unknown type discriminator.</exception>
    public static EligibilityRule Deserialize(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            throw new ArgumentException("Cannot deserialize a null or empty eligibility rule JSON.", nameof(json));

        JsonNode? node;
        try
        {
            node = JsonNode.Parse(json);
        }
        catch (JsonException ex)
        {
            throw new ArgumentException($"Eligibility rule JSON is malformed: {ex.Message}", nameof(json), ex);
        }

        if (node is null)
            throw new ArgumentException("Eligibility rule JSON parsed to null.", nameof(json));

        return ParseNode(node);
    }

    // ─── Serialization helpers ────────────────────────────────────────────────

    private static JsonNode BuildNode(EligibilityRule rule)
    {
        return rule switch
        {
            AllOfRule r => BuildComposite(AllOf, r.Rules),
            AnyOfRule r => BuildComposite(AnyOf, r.Rules),
            ContractActiveRule => new JsonObject { [TypeKey] = ContractActive },
            NoOverdueInstallmentRule => new JsonObject { [TypeKey] = NoOverdueInstallment },

            PaymentTermsEqualsRule r => new JsonObject
            {
                [TypeKey] = PaymentTermsEq,
                ["paymentTerms"] = r.PaymentTerms.ToString()
            },

            PaymentMethodEqualsRule r => new JsonObject
            {
                [TypeKey] = PaymentMethodEq,
                ["paymentMethod"] = r.PaymentMethod
            },

            CompletedByUtcRule r => new JsonObject
            {
                [TypeKey] = CompletedByUtcType,
                ["completedByUtc"] = r.CompletedBy.ToString("O")
            },

            AmountPaidAtLeastRule r => new JsonObject
            {
                [TypeKey] = AmountPaidAtLeast,
                ["amount"] = r.Amount
            },

            DaysFromContractStartGteRule r => new JsonObject
            {
                [TypeKey] = DaysFromContractStartGte,
                ["days"] = r.Days
            },

            DurationMonthsGteRule r => new JsonObject
            {
                [TypeKey] = DurationMonthsGte,
                ["months"] = r.Months
            },

            _ => throw new InvalidOperationException(
                $"Unknown EligibilityRule type: {rule.GetType().Name}. " +
                "The closed rule algebra must be extended explicitly.")
        };
    }

    private static JsonObject BuildComposite(string typeValue, IReadOnlyList<EligibilityRule> children)
    {
        var rulesArray = new JsonArray();
        foreach (var child in children)
            rulesArray.Add(BuildNode(child));

        return new JsonObject
        {
            [TypeKey] = typeValue,
            ["rules"] = rulesArray
        };
    }

    // ─── Deserialization helpers ──────────────────────────────────────────────

    private static EligibilityRule ParseNode(JsonNode node)
    {
        if (node is not JsonObject obj)
            throw new ArgumentException("Expected a JSON object for an EligibilityRule node.");

        var typeValue = ReadString(obj, TypeKey, "EligibilityRule");
        if (typeValue.Length == 0)
            throw new ArgumentException($"EligibilityRule JSON node is missing the required '{TypeKey}' property.");

        return typeValue switch
        {
            AllOf => new AllOfRule(ParseChildren(obj, typeValue)),
            AnyOf => new AnyOfRule(ParseChildren(obj, typeValue)),
            ContractActive => ContractActiveRule.Instance,
            NoOverdueInstallment => NoOverdueInstallmentRule.Instance,
            PaymentTermsEq => ParsePaymentTermsEq(obj),
            PaymentMethodEq => ParsePaymentMethodEq(obj),
            CompletedByUtcType => ParseCompletedByUtc(obj),
            AmountPaidAtLeast => ParseAmountPaidAtLeast(obj),
            DaysFromContractStartGte => ParseDaysFromContractStartGte(obj),
            DurationMonthsGte => ParseDurationMonthsGte(obj),
            _ => throw new ArgumentException(
                $"Unknown eligibility rule type discriminator: '{typeValue}'. " +
                "No executable code can be supplied through the serialized representation.")
        };
    }

    private static IEnumerable<EligibilityRule> ParseChildren(JsonObject obj, string parentType)
    {
        var rulesNode = obj["rules"];
        if (rulesNode is not JsonArray rulesArray)
            throw new ArgumentException(
                $"'{parentType}' rule JSON is missing the 'rules' array.");

        if (rulesArray.Count == 0)
            throw new ArgumentException(
                $"'{parentType}' rule JSON has an empty 'rules' array (at least one child rule required).");

        var list = new List<EligibilityRule>(rulesArray.Count);
        for (int i = 0; i < rulesArray.Count; i++)
        {
            var child = rulesArray[i];
            if (child is null)
                throw new ArgumentException(
                    $"'{parentType}' rule JSON has a null child at index {i}.");
            list.Add(ParseNode(child));
        }
        return list;
    }

    /// <summary>
    /// Reads a required JSON string property. A malformed payload (missing key, null value, or a
    /// non-string JSON value) is always reported as <see cref="ArgumentException"/> so callers never
    /// observe a raw serializer exception leaking out of the closed rule algebra.
    /// </summary>
    private static string ReadString(JsonObject obj, string key, string parentType)
    {
        var node = obj[key];
        if (node is null)
            throw new ArgumentException(
                $"'{parentType}' rule JSON is missing the required '{key}' property.");

        try
        {
            return node.GetValue<string>() ?? string.Empty;
        }
        catch (Exception ex) when (ex is InvalidOperationException or JsonException)
        {
            throw new ArgumentException(
                $"'{parentType}' rule JSON property '{key}' is not a JSON string.");
        }
    }

    private static EligibilityRule ParsePaymentTermsEq(JsonObject obj)
    {
        var rawValue = ReadString(obj, "paymentTerms", PaymentTermsEq);
        if (string.IsNullOrWhiteSpace(rawValue))
            throw new ArgumentException("'payment_terms_eq' JSON is missing 'paymentTerms'.");

        if (!Enum.TryParse<PaymentTerms>(rawValue, ignoreCase: false, out var paymentTerms))
            throw new ArgumentException(
                $"'payment_terms_eq' JSON has unknown paymentTerms value: '{rawValue}'.");

        // Use the domain factory which performs the same Enum.IsDefined guard
        return EligibilityRule.PaymentTermsEquals(paymentTerms);
    }

    private static EligibilityRule ParsePaymentMethodEq(JsonObject obj)
    {
        var value = ReadString(obj, "paymentMethod", PaymentMethodEq);
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException("'payment_method_eq' JSON is missing 'paymentMethod'.");

        return EligibilityRule.PaymentMethodEquals(value);
    }

    private static EligibilityRule ParseCompletedByUtc(JsonObject obj)
    {
        var raw = obj["completedByUtc"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(raw))
            throw new ArgumentException("'completed_by_utc' JSON is missing 'completedByUtc'.");

        if (!DateTime.TryParseExact(raw, "O", System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.RoundtripKind, out var dt))
            throw new ArgumentException(
                $"'completed_by_utc' JSON has invalid 'completedByUtc' value: '{raw}'.");

        return EligibilityRule.CompletedByUtc(dt);
    }

    private static EligibilityRule ParseAmountPaidAtLeast(JsonObject obj)
    {
        var amountNode = obj["amount"];
        if (amountNode is null)
            throw new ArgumentException("'amount_paid_at_least' JSON is missing 'amount'.");

        decimal amount;
        try { amount = amountNode.GetValue<decimal>(); }
        catch { throw new ArgumentException("'amount_paid_at_least' JSON has invalid 'amount' value."); }

        return EligibilityRule.AmountPaidAtLeast(amount);
    }

    private static EligibilityRule ParseDaysFromContractStartGte(JsonObject obj)
    {
        var daysNode = obj["days"];
        if (daysNode is null)
            throw new ArgumentException("'days_from_contract_start_gte' JSON is missing 'days'.");

        int days;
        try { days = daysNode.GetValue<int>(); }
        catch { throw new ArgumentException("'days_from_contract_start_gte' JSON has invalid 'days' value."); }

        return EligibilityRule.DaysFromContractStartGte(days);
    }

    private static EligibilityRule ParseDurationMonthsGte(JsonObject obj)
    {
        var monthsNode = obj["months"];
        if (monthsNode is null)
            throw new ArgumentException("'duration_months_gte' JSON is missing 'months'.");

        int months;
        try { months = monthsNode.GetValue<int>(); }
        catch { throw new ArgumentException("'duration_months_gte' JSON has invalid 'months' value."); }

        return EligibilityRule.DurationMonthsGte(months);
    }
}
