using System;
using Unity.Netcode;
using UnityEngine;

/// <summary>
/// Estado de una mano publicado por el owner del avatar y leido por todos los clientes.
/// Las posiciones estan en WORLD SPACE para que cada cliente pueda colocar los markers
/// sin saber donde esta el XR Origin del owner.
/// Incluye tambien el estado del raycast asociado a la mano (para visualizar el ray remoto).
/// </summary>
public struct HandPoseState : INetworkSerializable, IEquatable<HandPoseState>
{
    public bool tracked;
    public bool pinching;
    public Vector3 thumbTipPos;
    public Vector3 indexTipPos;
    public Vector3 palmPos;
    public Quaternion palmRot;

    public bool rayActive;
    public Vector3 rayStart;
    public Vector3 rayEnd;

    public void NetworkSerialize<T>(BufferSerializer<T> s) where T : IReaderWriter
    {
        s.SerializeValue(ref tracked);
        s.SerializeValue(ref pinching);
        s.SerializeValue(ref thumbTipPos);
        s.SerializeValue(ref indexTipPos);
        s.SerializeValue(ref palmPos);
        s.SerializeValue(ref palmRot);
        s.SerializeValue(ref rayActive);
        s.SerializeValue(ref rayStart);
        s.SerializeValue(ref rayEnd);
    }

    public bool Equals(HandPoseState other)
    {
        return tracked == other.tracked
            && pinching == other.pinching
            && thumbTipPos == other.thumbTipPos
            && indexTipPos == other.indexTipPos
            && palmPos == other.palmPos
            && palmRot == other.palmRot
            && rayActive == other.rayActive
            && rayStart == other.rayStart
            && rayEnd == other.rayEnd;
    }
}
