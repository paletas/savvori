using System.Linq.Expressions;
using Savvori.Shared;
using Savvori.WebApi.Scraping;

namespace Savvori.WebApi.Services;

/// <summary>One thing the user typed: a glossary term (matched as any of its EN/PT equivalents) or a plain word.</summary>
/// <param name="Text">The folded text as typed (a word, or a glossary phrase such as "olive oil").</param>
/// <param name="Alternatives">Every folded term that satisfies this concept; just <paramref name="Text"/> when not in the glossary.</param>
public sealed record SearchConcept(string Text, IReadOnlyList<string> Alternatives)
{
    public bool InGlossary => Alternatives.Count > 1 || SearchGlossary.Lookup(Text) is not null;
}

/// <summary>
/// Turns a product search string into a language-agnostic filter and a relevance order, both translated to SQL
/// (so paging and totals stay in the database). Words are ANDed; within one word, its EN/PT glossary equivalents
/// are ORed. Glossary terms match as whole words in the product name and in its search aliases; words outside the
/// glossary keep the original substring behaviour, so typing "arro" still finds "Arroz". Order: names that start
/// with a searched term, then names where every term is a word (or word prefix), then everything else (substring,
/// brand or alias only); ties fall back to the name so results are stable across pages.
/// </summary>
public static class ProductSearchQuery
{
    public static IReadOnlyList<SearchConcept> Parse(string? search)
    {
        var folded = ProductNormalizer.Normalize(search ?? string.Empty);
        if (folded.Length == 0)
        {
            // Nothing searchable once folded (punctuation only): keep matching the raw text against the name.
            var raw = (search ?? string.Empty).Trim().ToLowerInvariant();
            return raw.Length == 0 ? [] : [new SearchConcept(raw, [raw])];
        }

        var words = folded.Split(' ');
        var concepts = new List<SearchConcept>();
        for (var i = 0; i < words.Length;)
        {
            // Longest glossary phrase first, so "olive oil" is one concept and not olive AND oil.
            SearchConcept? found = null;
            var length = 0;
            for (var n = Math.Min(SearchGlossary.MaxPhraseWords, words.Length - i); n >= 1 && found is null; n--)
            {
                var phrase = string.Join(' ', words.Skip(i).Take(n));
                if (SearchGlossary.Lookup(phrase) is { } group)
                {
                    found = new SearchConcept(phrase, group);
                    length = n;
                }
            }

            if (found is null)
            {
                found = new SearchConcept(words[i], [words[i]]);
                length = 1;
            }
            concepts.Add(found);
            i += length;
        }
        return concepts;
    }

    /// <summary>Narrows <paramref name="products"/> to the matches and orders them by relevance.</summary>
    public static IOrderedQueryable<Product> Apply(IQueryable<Product> products, string search)
    {
        var concepts = Parse(search);
        if (concepts.Count == 0)
            return products.OrderBy(p => p.Name).ThenBy(p => p.Id);

        var match = And(concepts.Select(MatchConcept));
        var startsWith = Or(concepts.Select(StartsWithConcept));
        var wordMatch = And(concepts.Select(WordMatchConcept));
        var tier = Tier(startsWith, wordMatch);

        return products.Where(match)
            .OrderBy(tier)
            .ThenBy(p => p.NormalizedName ?? p.Name)
            .ThenBy(p => p.Id);
    }

    // A glossary concept matches whole words in the name or an alias. A plain word keeps substring matching on
    // the name, brand and aliases (the term is folded, so accents and case in the query do not matter).
    private static Expression<Func<Product, bool>> MatchConcept(SearchConcept c) =>
        c.InGlossary
            ? Or(c.Alternatives.Select(WholeWord))
            : Substring(c.Text);

    private static Expression<Func<Product, bool>> Substring(string term) => p =>
        p.NormalizedName != null && p.NormalizedName.Contains(term) ||
        p.Name.ToLower().Contains(term) ||
        p.Brand != null && p.Brand.ToLower().Contains(term) ||
        p.SearchAliases.Any(a => a.SearchText.Contains(term));

    private static Expression<Func<Product, bool>> WholeWord(string term) => p =>
        p.NormalizedName != null && (" " + p.NormalizedName + " ").Contains(" " + term + " ") ||
        p.SearchAliases.Any(a => (" " + a.SearchText + " ").Contains(" " + term + " "));

    // Tier 0: the product name starts with the searched term (or one of its equivalents).
    private static Expression<Func<Product, bool>> StartsWithConcept(SearchConcept c) =>
        Or(c.Alternatives.Select<string, Expression<Func<Product, bool>>>(a =>
            c.InGlossary
                ? p => p.NormalizedName != null && (p.NormalizedName == a || p.NormalizedName.StartsWith(a + " "))
                : p => p.NormalizedName != null && p.NormalizedName.StartsWith(a)));

    // Tier 1: the term is a word (or, for plain words, a word prefix) of the product name itself,
    // as opposed to a substring inside a longer word or a brand/alias-only hit.
    private static Expression<Func<Product, bool>> WordMatchConcept(SearchConcept c) =>
        Or(c.Alternatives.Select<string, Expression<Func<Product, bool>>>(a =>
            c.InGlossary
                ? p => p.NormalizedName != null && (" " + p.NormalizedName + " ").Contains(" " + a + " ")
                : p => p.NormalizedName != null && (" " + p.NormalizedName).Contains(" " + a)));

    private static Expression<Func<Product, int>> Tier(
        Expression<Func<Product, bool>> startsWith, Expression<Func<Product, bool>> wordMatch)
    {
        var p = Expression.Parameter(typeof(Product), "p");
        var body = Expression.Condition(
            Rebind(startsWith, p), Expression.Constant(0),
            Expression.Condition(Rebind(wordMatch, p), Expression.Constant(1), Expression.Constant(2)));
        return Expression.Lambda<Func<Product, int>>(body, p);
    }

    private static Expression<Func<Product, bool>> Or(IEnumerable<Expression<Func<Product, bool>>> parts) =>
        Combine(parts, Expression.OrElse);

    private static Expression<Func<Product, bool>> And(IEnumerable<Expression<Func<Product, bool>>> parts) =>
        Combine(parts, Expression.AndAlso);

    private static Expression<Func<Product, bool>> Combine(
        IEnumerable<Expression<Func<Product, bool>>> parts, Func<Expression, Expression, BinaryExpression> join)
    {
        var p = Expression.Parameter(typeof(Product), "p");
        var body = parts.Select(part => Rebind(part, p)).Aggregate((left, right) => join(left, right));
        return Expression.Lambda<Func<Product, bool>>(body, p);
    }

    private static Expression Rebind(LambdaExpression lambda, ParameterExpression parameter) =>
        new ParameterReplacer(lambda.Parameters[0], parameter).Visit(lambda.Body);

    private sealed class ParameterReplacer(ParameterExpression from, ParameterExpression to) : ExpressionVisitor
    {
        protected override Expression VisitParameter(ParameterExpression node) => node == from ? to : base.VisitParameter(node);
    }
}
