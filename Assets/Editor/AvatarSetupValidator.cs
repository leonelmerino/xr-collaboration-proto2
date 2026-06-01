#if UNITY_EDITOR
using System.Text;
using UnityEditor;
using UnityEngine;
using UnityEngine.Animations.Rigging;

/// <summary>
/// Editor tool: valida el setup del AvatarHumanoid.prefab seleccionado y reporta que
/// falta. Util cuando el avatar se ve "desarmado" o no responde a la pose tracking.
///
/// Como usar:
/// - Seleccionar el prefab AvatarHumanoid.prefab en Project window (o un GameObject
///   instanciado en escena que sea el root del avatar).
/// - Menu: XR Collab > Avatars > Validate Avatar Setup.
/// - Lee en Console el reporte detallado.
/// </summary>
public static class AvatarSetupValidator
{
    [MenuItem("XR Collab/Avatars/Validate Avatar Setup")]
    public static void Validate()
    {
        var sel = Selection.activeGameObject;
        if (sel == null)
        {
            EditorUtility.DisplayDialog("Avatar Validator",
                "Selecciona el prefab AvatarHumanoid (en Project o en escena) y vuelve a correr.",
                "OK");
            return;
        }

        // Si es un asset, lo abrimos para inspeccionar como instancia.
        GameObject root = sel;

        var sb = new StringBuilder();
        sb.AppendLine($"=== Avatar Setup Validation: '{root.name}' ===\n");

        int problems = 0;

        // --- Root level checks ---
        sb.AppendLine("Root components:");
        problems += Check(sb, root.GetComponent<Unity.Netcode.NetworkObject>() != null,
            "NetworkObject", "presente", "FALTA — el avatar no se va a spawnear via NGO.");

        problems += Check(sb, root.GetComponent<NetworkedAvatarRole>() != null,
            "NetworkedAvatarRole", "presente", "FALTA — el switching de mesh por rol no funciona.");

        var poseSync = root.GetComponent<NetworkedAvatarPose>();
        problems += Check(sb, poseSync != null,
            "NetworkedAvatarPose", "presente", "FALTA — la cabeza y manos no se van a sincronizar.");

        var roleSwitcher = root.GetComponent<AvatarRoleMeshSwitcher>();
        problems += Check(sb, roleSwitcher != null,
            "AvatarRoleMeshSwitcher", "presente", "FALTA — todos los sub-meshes van a estar visibles a la vez.");

        // --- AvatarVisuals + sub-meshes ---
        var avatarVisuals = root.transform.Find("AvatarVisuals");
        sb.AppendLine();
        sb.AppendLine("AvatarVisuals children:");
        if (avatarVisuals == null)
        {
            sb.AppendLine("  [!] No se encuentra child 'AvatarVisuals'. Verifica jerarquia.");
            problems++;
        }
        else
        {
            CheckSubMesh(sb, avatarVisuals, "Avatar_Host", ref problems);
            CheckSubMesh(sb, avatarVisuals, "Avatar_Client", ref problems);
            CheckSubMesh(sb, avatarVisuals, "Avatar_Helper", ref problems);
        }

        sb.AppendLine();
        if (problems == 0)
            sb.AppendLine("[OK] No se detectaron problemas evidentes en la jerarquia.");
        else
            sb.AppendLine($"[!] {problems} problemas detectados. Revisa los puntos marcados arriba.");

        Debug.Log(sb.ToString());
        EditorUtility.DisplayDialog("Avatar Validator",
            $"{problems} problemas detectados. Ver Console para detalle.", "OK");
    }

    private static int Check(StringBuilder sb, bool ok, string label, string okMsg, string failMsg)
    {
        sb.AppendLine($"  - {label}: {(ok ? "✓ " + okMsg : "✗ " + failMsg)}");
        return ok ? 0 : 1;
    }

    private static void CheckSubMesh(StringBuilder sb, Transform parent, string name, ref int problems)
    {
        var t = parent.Find(name);
        sb.AppendLine($"  [{name}]");
        if (t == null)
        {
            sb.AppendLine($"    ✗ FALTA. No hay child con ese nombre.");
            problems++;
            return;
        }

        var go = t.gameObject;

        // Animator
        var animator = go.GetComponent<Animator>();
        if (animator == null)
        {
            sb.AppendLine($"    ✗ Animator FALTA.");
            problems++;
        }
        else if (!animator.isHuman)
        {
            sb.AppendLine($"    ✗ Animator Animation Type NO es Humanoid (es {(animator.avatar?.isHuman ?? false ? "Humano" : "Generic/Legacy")}). " +
                          "Cambiar en el .fbx → Rig tab → Humanoid + Apply.");
            problems++;
        }
        else
        {
            var headBone = animator.GetBoneTransform(HumanBodyBones.Head);
            sb.AppendLine($"    ✓ Animator Humanoid OK. Head bone: {(headBone != null ? headBone.name : "NO ENCONTRADO")}.");
            if (headBone == null) problems++;
        }

        // AvatarPoseDriver
        var driver = go.GetComponent<AvatarPoseDriver>();
        if (driver == null)
        {
            sb.AppendLine($"    ✗ AvatarPoseDriver FALTA. Sin esto, head + IK targets no se van a actualizar.");
            problems++;
        }
        else
        {
            // No podemos leer campos privados, pero podemos hacer una checa heuristica
            // si esta enabled.
            sb.AppendLine($"    ✓ AvatarPoseDriver presente. (Verifica refs en Inspector: PoseSync, Animator, IK Targets.)");
        }

        // Rig Builder
        var rigBuilder = go.GetComponent<RigBuilder>();
        if (rigBuilder == null)
        {
            sb.AppendLine($"    ✗ Rig Builder FALTA (de Animation Rigging package).");
            problems++;
        }
        else if (rigBuilder.layers == null || rigBuilder.layers.Count == 0)
        {
            sb.AppendLine($"    ✗ Rig Builder presente PERO 'layers' vacio. Agregar el Rig child.");
            problems++;
        }
        else
        {
            sb.AppendLine($"    ✓ Rig Builder con {rigBuilder.layers.Count} layer(s).");
        }

        // Rig child
        var rigT = t.Find("Rig");
        if (rigT == null)
        {
            sb.AppendLine($"    ✗ Child 'Rig' FALTA.");
            problems++;
        }
        else
        {
            // IK constraints
            CheckIKConstraint(sb, rigT, "LeftArmIK", ref problems);
            CheckIKConstraint(sb, rigT, "RightArmIK", ref problems);
        }

        // IK target Transforms
        var leftTarget = t.Find("IKTarget_LeftHand");
        var rightTarget = t.Find("IKTarget_RightHand");
        if (leftTarget == null)
        {
            sb.AppendLine($"    ✗ IKTarget_LeftHand FALTA como child.");
            problems++;
        }
        if (rightTarget == null)
        {
            sb.AppendLine($"    ✗ IKTarget_RightHand FALTA como child.");
            problems++;
        }
    }

    private static void CheckIKConstraint(StringBuilder sb, Transform parent, string name, ref int problems)
    {
        var t = parent.Find(name);
        if (t == null)
        {
            sb.AppendLine($"    ✗ Constraint '{name}' FALTA (esperado child del Rig).");
            problems++;
            return;
        }

        var twoBone = t.GetComponent<TwoBoneIKConstraint>();
        if (twoBone == null)
        {
            sb.AppendLine($"    ✗ '{name}' no tiene Two Bone IK Constraint.");
            problems++;
            return;
        }

        var data = twoBone.data;
        bool ok = data.root != null && data.mid != null && data.tip != null && data.target != null;
        if (ok)
            sb.AppendLine($"    ✓ '{name}' bones+target OK (Root={data.root.name}, Mid={data.mid.name}, Tip={data.tip.name}, Target={data.target.name}).");
        else
        {
            sb.AppendLine($"    ✗ '{name}' tiene referencias FALTANTES: " +
                          $"Root={(data.root?.name ?? "NONE")}, Mid={(data.mid?.name ?? "NONE")}, " +
                          $"Tip={(data.tip?.name ?? "NONE")}, Target={(data.target?.name ?? "NONE")}.");
            problems++;
        }
    }
}
#endif
