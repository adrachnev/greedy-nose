using GreedyNose.Api.Domain;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace GreedyNose.Api.Data;

/// <summary>
/// Stores <see cref="Classification"/> as the app's own <c>good</c>/<c>bad</c> text instead of
/// <c>HasConversion&lt;string&gt;()</c>'s member names. The strings themselves live in
/// <see cref="ClassificationText"/>, shared with the check constraint in
/// <see cref="GreedyNoseDbContext"/>; the enum stays in C# so the rule engine (step 5) gets a
/// compiler-checked <c>switch</c> instead of comparing strings.
///
/// An unknown stored value throws (see <see cref="ClassificationText"/>). The
/// <c>CK_Rules_Classification</c> constraint makes that unreachable from the database side; this
/// is the second line.
/// </summary>
public sealed class ClassificationConverter() : ValueConverter<Classification, string>(
    value => value.ToText(),
    text => ClassificationText.Parse(text));
