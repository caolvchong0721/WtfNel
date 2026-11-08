namespace WtfNel.Core.Auth.Login;

/// <summary>Asks the user to solve a 4399 captcha. Coordinates are in the original image's pixels.</summary>
public interface ICaptchaPrompt
{
    /// <summary>Text captcha: returns the characters shown in the image.</summary>
    string Image(byte[] image);

    /// <summary>Slider captcha: returns how far (in background pixels) the piece must move right.</summary>
    int Jigsaw(byte[] background, byte[] piece);

    /// <summary>Click captcha: returns the click points, in the order the characters are listed in <paramref name="text"/>.</summary>
    IReadOnlyList<(int X, int Y)> Click(byte[] image, string text);
}

/// <summary>Reads width/height from a PNG, JPEG or GIF header.</summary>
public static class ImageSize
{
    public static (int Width, int Height)? Read(byte[] data)
    {
        if (data.Length > 24 && data[0] == 0x89 && data[1] == 'P' && data[2] == 'N' && data[3] == 'G')
            return (BigEndian(data, 16), BigEndian(data, 20));
        if (data.Length > 10 && data[0] == 'G' && data[1] == 'I' && data[2] == 'F')
            return (data[6] | data[7] << 8, data[8] | data[9] << 8);
        if (data.Length > 4 && data[0] == 0xFF && data[1] == 0xD8)
        {
            var i = 2;
            while (i + 9 < data.Length)
            {
                if (data[i] != 0xFF) { i++; continue; }
                var marker = data[i + 1];
                if (marker is >= 0xC0 and <= 0xCF and not 0xC4 and not 0xC8 and not 0xCC)
                    return (data[i + 7] << 8 | data[i + 8], data[i + 5] << 8 | data[i + 6]);
                i += 2 + (data[i + 2] << 8 | data[i + 3]);
            }
        }
        return null;
    }

    private static int BigEndian(byte[] d, int o) => d[o] << 24 | d[o + 1] << 16 | d[o + 2] << 8 | d[o + 3];
}
