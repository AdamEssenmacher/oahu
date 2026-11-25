namespace AdamE.Oahu;

/// <summary>Marks an interface as a contract: the C# side of one native screen.</summary>
/// <remarks>
/// <para>
/// A contract declares what its screen can observe and what it can ask for, and nothing else. Its streams are get-only
/// properties of the stream types, such as <see cref="IOahuState{T}"/> and <see cref="IOahuEvents{T}"/>. A view model
/// implements the contract, and Oahu generates the code on both sides of the seam from it.
/// </para>
/// <para>
/// A contract's streams are numbered in the order they are declared, and its members are numbered separately, in the
/// order they are declared. So the order matters, and both sides of an app must be built from the same contract. How a
/// contract split across partial declarations, or one that inherits from another interface, is ordered is defined with
/// the contract model, in a later milestone.
/// </para>
/// </remarks>
[AttributeUsage(AttributeTargets.Interface, Inherited = false)]
public sealed class OahuContractAttribute : Attribute;
