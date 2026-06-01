using Unity.Collections;
using Unity.Netcode;

/// <summary>
/// Struct serializable por NGO que asocia un ClientId con el nodeId del participante.
/// Usado por JengaTurnManager para que todos los clientes puedan resolver nombres.
/// </summary>
public struct ParticipantInfo : INetworkSerializable, System.IEquatable<ParticipantInfo>
{
    public ulong ClientId;
    public FixedString64Bytes NodeId;

    public void NetworkSerialize<T>(BufferSerializer<T> serializer) where T : IReaderWriter
    {
        serializer.SerializeValue(ref ClientId);
        serializer.SerializeValue(ref NodeId);
    }

    public bool Equals(ParticipantInfo other) => ClientId == other.ClientId;
    public override int GetHashCode() => ClientId.GetHashCode();
}
