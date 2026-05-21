using System;
using Unity.Netcode;
using UnityEngine;

/// <summary>
/// Estado sincronizado del esqueleto de manos de un avatar.
/// 20 joints por mano (sin Palm ni Metacarpales) en world space, mas un flag de tracking
/// por mano. El indexing coincide con HandSkeletonRenderer.s_jointIds y s_fingerChains
/// (Wrist=0, ThumbProximal=1, ..., LittleTip=19).
///
/// Implementacion: campos explicitos (l00..l19, r00..r19) en vez de arrays.
/// Por que: NetworkVariable&lt;T&gt; copia por valor y los arrays mantendrian referencia
/// compartida entre la copia "previous" y "new" del NGO, lo que rompe el deduplicate.
/// Tambien evita codigo unsafe asi no hace falta tocar el Player Settings del proyecto.
///
/// Bandwidth: 2 bools + 40 Vector3 = ~482 bytes por update. A 30 Hz del tick rate NGO
/// es ~14.5 KB/s por avatar. Triada = ~43 KB/s. Trivial en LAN.
/// </summary>
public struct HandSkeletonState : INetworkSerializable, IEquatable<HandSkeletonState>
{
    public const int JointsPerHand = 20;

    public bool leftTracked;
    public bool rightTracked;

    // Mano izquierda. Indices: 0=Wrist, 1-3=Pulgar, 4-7=Indice, 8-11=Medio, 12-15=Anular, 16-19=Menique.
    public Vector3 l00, l01, l02, l03, l04, l05, l06, l07, l08, l09;
    public Vector3 l10, l11, l12, l13, l14, l15, l16, l17, l18, l19;

    // Mano derecha, mismo indexing.
    public Vector3 r00, r01, r02, r03, r04, r05, r06, r07, r08, r09;
    public Vector3 r10, r11, r12, r13, r14, r15, r16, r17, r18, r19;

    public Vector3 GetLeft(int i)
    {
        switch (i)
        {
            case 0:  return l00; case 1:  return l01; case 2:  return l02; case 3:  return l03; case 4:  return l04;
            case 5:  return l05; case 6:  return l06; case 7:  return l07; case 8:  return l08; case 9:  return l09;
            case 10: return l10; case 11: return l11; case 12: return l12; case 13: return l13; case 14: return l14;
            case 15: return l15; case 16: return l16; case 17: return l17; case 18: return l18; case 19: return l19;
            default: return Vector3.zero;
        }
    }

    public Vector3 GetRight(int i)
    {
        switch (i)
        {
            case 0:  return r00; case 1:  return r01; case 2:  return r02; case 3:  return r03; case 4:  return r04;
            case 5:  return r05; case 6:  return r06; case 7:  return r07; case 8:  return r08; case 9:  return r09;
            case 10: return r10; case 11: return r11; case 12: return r12; case 13: return r13; case 14: return r14;
            case 15: return r15; case 16: return r16; case 17: return r17; case 18: return r18; case 19: return r19;
            default: return Vector3.zero;
        }
    }

    public void SetLeft(int i, Vector3 v)
    {
        switch (i)
        {
            case 0:  l00 = v; break; case 1:  l01 = v; break; case 2:  l02 = v; break; case 3:  l03 = v; break; case 4:  l04 = v; break;
            case 5:  l05 = v; break; case 6:  l06 = v; break; case 7:  l07 = v; break; case 8:  l08 = v; break; case 9:  l09 = v; break;
            case 10: l10 = v; break; case 11: l11 = v; break; case 12: l12 = v; break; case 13: l13 = v; break; case 14: l14 = v; break;
            case 15: l15 = v; break; case 16: l16 = v; break; case 17: l17 = v; break; case 18: l18 = v; break; case 19: l19 = v; break;
        }
    }

    public void SetRight(int i, Vector3 v)
    {
        switch (i)
        {
            case 0:  r00 = v; break; case 1:  r01 = v; break; case 2:  r02 = v; break; case 3:  r03 = v; break; case 4:  r04 = v; break;
            case 5:  r05 = v; break; case 6:  r06 = v; break; case 7:  r07 = v; break; case 8:  r08 = v; break; case 9:  r09 = v; break;
            case 10: r10 = v; break; case 11: r11 = v; break; case 12: r12 = v; break; case 13: r13 = v; break; case 14: r14 = v; break;
            case 15: r15 = v; break; case 16: r16 = v; break; case 17: r17 = v; break; case 18: r18 = v; break; case 19: r19 = v; break;
        }
    }

    public void NetworkSerialize<T>(BufferSerializer<T> s) where T : IReaderWriter
    {
        s.SerializeValue(ref leftTracked);
        s.SerializeValue(ref l00); s.SerializeValue(ref l01); s.SerializeValue(ref l02); s.SerializeValue(ref l03); s.SerializeValue(ref l04);
        s.SerializeValue(ref l05); s.SerializeValue(ref l06); s.SerializeValue(ref l07); s.SerializeValue(ref l08); s.SerializeValue(ref l09);
        s.SerializeValue(ref l10); s.SerializeValue(ref l11); s.SerializeValue(ref l12); s.SerializeValue(ref l13); s.SerializeValue(ref l14);
        s.SerializeValue(ref l15); s.SerializeValue(ref l16); s.SerializeValue(ref l17); s.SerializeValue(ref l18); s.SerializeValue(ref l19);

        s.SerializeValue(ref rightTracked);
        s.SerializeValue(ref r00); s.SerializeValue(ref r01); s.SerializeValue(ref r02); s.SerializeValue(ref r03); s.SerializeValue(ref r04);
        s.SerializeValue(ref r05); s.SerializeValue(ref r06); s.SerializeValue(ref r07); s.SerializeValue(ref r08); s.SerializeValue(ref r09);
        s.SerializeValue(ref r10); s.SerializeValue(ref r11); s.SerializeValue(ref r12); s.SerializeValue(ref r13); s.SerializeValue(ref r14);
        s.SerializeValue(ref r15); s.SerializeValue(ref r16); s.SerializeValue(ref r17); s.SerializeValue(ref r18); s.SerializeValue(ref r19);
    }

    public bool Equals(HandSkeletonState o)
    {
        if (leftTracked != o.leftTracked || rightTracked != o.rightTracked) return false;
        if (l00 != o.l00 || l01 != o.l01 || l02 != o.l02 || l03 != o.l03 || l04 != o.l04 ||
            l05 != o.l05 || l06 != o.l06 || l07 != o.l07 || l08 != o.l08 || l09 != o.l09 ||
            l10 != o.l10 || l11 != o.l11 || l12 != o.l12 || l13 != o.l13 || l14 != o.l14 ||
            l15 != o.l15 || l16 != o.l16 || l17 != o.l17 || l18 != o.l18 || l19 != o.l19) return false;
        if (r00 != o.r00 || r01 != o.r01 || r02 != o.r02 || r03 != o.r03 || r04 != o.r04 ||
            r05 != o.r05 || r06 != o.r06 || r07 != o.r07 || r08 != o.r08 || r09 != o.r09 ||
            r10 != o.r10 || r11 != o.r11 || r12 != o.r12 || r13 != o.r13 || r14 != o.r14 ||
            r15 != o.r15 || r16 != o.r16 || r17 != o.r17 || r18 != o.r18 || r19 != o.r19) return false;
        return true;
    }
}
