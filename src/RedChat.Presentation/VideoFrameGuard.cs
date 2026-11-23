namespace RedChat.Presentation;

/// <summary>
/// Contrôle de cohérence d'une image vidéo brute AVANT son affichage.
/// POURQUOI ici (et pas dans la fenêtre WPF) : une image incohérente (stride trop petit, tampon trop court,
/// dimensions absurdes) fait lever une exception à l'écriture du bitmap, dans un rappel du dispatcher, ce qui
/// arrête toute l'application. Cette règle est pure (aucun type WPF/SIPSorcery) donc testable automatiquement.
/// </summary>
public static class VideoFrameGuard
{
    /// <summary>Côté maximal accepté (pixels) : au-delà, l'image est refusée plutôt qu'allouée.</summary>
    public const uint MaxSide = 8192;

    /// <summary>Octets par pixel du format Bgr24 affiché.</summary>
    public const int BytesPerPixelBgr24 = 3;

    /// <summary>Vrai si une image Bgr24 de ces dimensions tient réellement dans un tampon de cette taille.</summary>
    /// <param name="bufferLength">Taille réelle du tampon de pixels, en octets.</param>
    /// <param name="width">Largeur en pixels.</param>
    /// <param name="height">Hauteur en pixels.</param>
    /// <param name="stride">Octets par ligne.</param>
    public static bool IsUsableBgr24(int bufferLength, uint width, uint height, int stride)
    {
        if (bufferLength <= 0 || width == 0 || height == 0 || width > MaxSide || height > MaxSide)
        {
            return false;
        }

        long minimumStride = (long)width * BytesPerPixelBgr24;
        return stride >= minimumStride && (long)stride * height <= bufferLength;
    }
}
