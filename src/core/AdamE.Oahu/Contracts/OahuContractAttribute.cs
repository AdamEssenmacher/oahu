namespace AdamE.Oahu;

/// <summary>Marks an interface as a contract: the C# side of one native screen.</summary>
/// <remarks>
/// <para>
/// A contract declares what its screen can observe and what it can ask for, and nothing else. Its streams are get-only
/// properties of the stream types <see cref="IOahuState{T}"/>, <see cref="IOahuEditable{T}"/>,
/// <see cref="IOahuEvents{T}"/> and <see cref="IOahuKeyedList{T}"/>, which carry values to the screen. Its members are
/// what the screen calls: its methods, and the <see cref="IOahuEditable{T}.Edit"/> of each editable. A view model
/// implements the contract, and Oahu generates the code on both sides of the seam from it.
/// </para>
/// <para>
/// A method returns <see langword="void"/> or <see cref="System.Threading.Tasks.Task"/>, and takes no parameter or one
/// contract value. A <see cref="System.Threading.Tasks.Task"/> method may also take a
/// <see cref="System.Threading.CancellationToken"/> as its last parameter, which is not on the wire and is cancelled
/// when the screen that made the call closes. A method returns nothing to the screen, which learns the outcome from the
/// streams. A contract declares no other kind of member.
/// </para>
/// <para>
/// A contract is one declaration of a non-generic interface: it is not partial, extends no interface and declares no
/// nested type, and it and every type it is nested in are public or internal. Its stream and method names differ in
/// more than letter case.
/// </para>
/// <para>
/// A contract's streams are numbered in the order they are declared, and its members are numbered separately. Its
/// methods keep the order they are declared in; where each edit falls among them is defined with the contract model, in
/// a later milestone. So the order matters, and both sides of an app must be built from the same contract.
/// </para>
/// </remarks>
[AttributeUsage(AttributeTargets.Interface, Inherited = false)]
public sealed class OahuContractAttribute : Attribute;
