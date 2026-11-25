namespace AdamE.Oahu.Wire;

/// <summary>Reads one value of type <typeparamref name="T"/> from <paramref name="reader"/> and moves it past the value.</summary>
/// <remarks>
/// <see cref="OahuReader.ReadList{T}"/> calls it once for each item. Pass a method group of a static method or a
/// <see langword="static"/> lambda, which the compiler creates once and caches. A lambda that captures a variable
/// allocates a new delegate each time the code that creates it runs.
/// </remarks>
/// <typeparam name="T">The type of the value read.</typeparam>
/// <param name="reader">The reader to read the value from.</param>
/// <returns>The value read.</returns>
public delegate T OahuReadFunc<out T>(ref OahuReader reader);
