using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Media.Imaging;

namespace TeyvatRoutine;
public static class Vision
{
    public static Bitmap Decode(Checkpoint c)
    {
        if (c.PngBase64.Length > 4 * 1024 * 1024) throw new InvalidDataException("확인 이미지가 너무 큽니다.");
        using var stream = new MemoryStream(Convert.FromBase64String(c.PngBase64));
        using var loaded = new Bitmap(stream);
        if (loaded.Width != c.Width || loaded.Height != c.Height || loaded.Width > 800 || loaded.Height > 500) throw new InvalidDataException("이미지 크기 오류");
        return loaded.Clone(new Rectangle(0, 0, loaded.Width, loaded.Height), PixelFormat.Format24bppRgb);
    }
    public static void Validate(Checkpoint c) { using var b = Decode(c); }
    public static Checkpoint Crop(Bitmap source, Rectangle region)
    {
        using var crop = source.Clone(region, PixelFormat.Format24bppRgb);
        using var stream = new MemoryStream(); crop.Save(stream, ImageFormat.Png);
        var c = new Checkpoint { X = region.X, Y = region.Y, Width = region.Width, Height = region.Height, PngBase64 = Convert.ToBase64String(stream.ToArray()) };
        var pixels = Pixels(crop); double sum = 0, squares = 0;
        for (int i = 0; i < pixels.Length; i += 3) { double luma = (pixels[i] + pixels[i + 1] + pixels[i + 2]) / 3.0; sum += luma; squares += luma * luma; }
        int count = pixels.Length / 3;
        if (Math.Sqrt(Math.Max(0, squares / count - Math.Pow(sum / count, 2))) < 10) throw new InvalidDataException("단색에 가까운 영역입니다. 글자나 고유한 아이콘을 포함해 선택하세요.");
        return c;
    }
    public static BitmapImage Image(Bitmap bitmap)
    {
        using var ms = new MemoryStream(); bitmap.Save(ms, ImageFormat.Png); ms.Position = 0;
        var image = new BitmapImage(); image.BeginInit(); image.CacheOption = BitmapCacheOption.OnLoad; image.StreamSource = ms; image.EndInit(); image.Freeze(); return image;
    }
    public static byte[] Pixels(Bitmap b)
    {
        var d = b.LockBits(new Rectangle(0, 0, b.Width, b.Height), ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);
        try
        {
            var result = new byte[b.Width * b.Height * 3];
            for (int y = 0; y < b.Height; y++) Marshal.Copy(IntPtr.Add(d.Scan0, y * d.Stride), result, y * b.Width * 3, b.Width * 3);
            return result;
        }
        finally { b.UnlockBits(d); }
    }
    public static double Score(Bitmap screen, Checkpoint c)
    {
        if (c.GoldTextOnly || c.WhiteForegroundOnly) return ScoreGoldText(screen, c);
        using var patch = Decode(c);
        var region = Rectangle.Intersect(new Rectangle(c.X - c.SearchRadius, c.Y - c.SearchRadius, c.Width + c.SearchRadius * 2, c.Height + c.SearchRadius * 2), new Rectangle(0, 0, screen.Width, screen.Height));
        if (region.Width < c.Width || region.Height < c.Height) return 0;
        using var nearby = screen.Clone(region, PixelFormat.Format24bppRgb);
        var s = Pixels(nearby); var t = Pixels(patch);
        int spacing = Math.Max(1, (int)Math.Sqrt(c.Width * c.Height / 1400.0));
        double best = 0;
        // Fixed screen region, with a small local translation search. No global object recognition.
        var offsets = Enumerable.Range(0, c.SearchRadius * 2 + 1).Where(i => i % 2 == 0).Select(i => i - c.SearchRadius).Append(0).Distinct().ToArray();
        foreach (int dy in offsets)
        foreach (int dx in offsets)
        {
            int left = c.X + dx, top = c.Y + dy;
            if (left < 0 || top < 0 || left + c.Width > screen.Width || top + c.Height > screen.Height) continue;
            long error = 0, samples = 0;
            double sumS = 0, sumT = 0, squareS = 0, squareT = 0, dot = 0;
            for (int y = 0; y < c.Height; y += spacing)
            for (int x = 0; x < c.Width; x += spacing)
            {
                int ti = (y * c.Width + x) * 3; int si = ((top - region.Top + y) * region.Width + left - region.Left + x) * 3;
                error += Math.Abs(s[si] - t[ti]) + Math.Abs(s[si + 1] - t[ti + 1]) + Math.Abs(s[si + 2] - t[ti + 2]); samples += 3;
                double sv = (s[si] + s[si + 1] + s[si + 2]) / 3.0, tv = (t[ti] + t[ti + 1] + t[ti + 2]) / 3.0;
                sumS += sv; sumT += tv; squareS += sv * sv; squareT += tv * tv; dot += sv * tv;
            }
            double count = samples / 3.0, varianceS = squareS - sumS * sumS / count, varianceT = squareT - sumT * sumT / count;
            if (varianceS < count * 9 || varianceT < count * 9) continue;
            double structure = Math.Clamp((dot - sumS * sumT / count) / Math.Sqrt(varianceS * varianceT), 0, 1);
            // Shape correlation tolerates brightness shifts; color still contributes evidence.
            best = Math.Max(best, .75 * structure + .25 * (1 - error / (255.0 * samples)));
        }
        return best;
    }
    private static double ScoreGoldText(Bitmap screen, Checkpoint c)
    {
        using var patch = Decode(c);
        var region = Rectangle.Intersect(new Rectangle(c.X-c.SearchRadius,c.Y-c.SearchRadius,c.Width+2*c.SearchRadius,c.Height+2*c.SearchRadius),new Rectangle(0,0,screen.Width,screen.Height));
        if (region.Width<c.Width || region.Height<c.Height) return 0;
        using var nearby = screen.Clone(region, PixelFormat.Format24bppRgb);
        bool[] Mask(Bitmap image)
        {
            var pixels=Pixels(image); var mask=new bool[image.Width*image.Height];
            for(int i=0;i<mask.Length;i++)
            {
                int b=pixels[3*i],g=pixels[3*i+1],r=pixels[3*i+2];
                mask[i]=c.WhiteForegroundOnly ? Math.Min(r,Math.Min(g,b))>=c.WhiteBrightness && Math.Max(r,Math.Max(g,b))-Math.Min(r,Math.Min(g,b))<=45 : r>=145 && g>=90 && r>g*1.05 && b<r*.60 && g>b*1.25;
            }
            return mask;
        }
        bool[] target=Mask(patch), scene=Mask(nearby);
        int targetCount=target.Count(v=>v);
        if(targetCount<40 || targetCount>target.Length*.7) return 0;
        bool Near(bool[] mask,int width,int height,int x,int y)
        {
            for(int dy=-1;dy<=1;dy++)for(int dx=-1;dx<=1;dx++)
            { int xx=x+dx,yy=y+dy;if(xx>=0 && yy>=0 && xx<width && yy<height && mask[yy*width+xx])return true; }
            return false;
        }
        double best=0;
        for(int dy=-c.SearchRadius;dy<=c.SearchRadius;dy++)for(int dx=-c.SearchRadius;dx<=c.SearchRadius;dx++)
        {
            int left=c.X+dx-region.X,top=c.Y+dy-region.Y;
            if(left<0 || top<0 || left+c.Width>region.Width || top+c.Height>region.Height)continue;
            int sceneCount=0,matchedScene=0,matchedTarget=0;
            for(int y=0;y<c.Height;y++)for(int x=0;x<c.Width;x++)
            {
                if(target[y*c.Width+x] && Near(scene,region.Width,region.Height,left+x,top+y))matchedTarget++;
                if(scene[(top+y)*region.Width+left+x])
                { sceneCount++;if(Near(target,c.Width,c.Height,x,y))matchedScene++; }
            }
            if(sceneCount==0)continue;
            double recall=matchedTarget/(double)targetCount,precision=matchedScene/(double)sceneCount;
            if(recall+precision>0)best=Math.Max(best,2*recall*precision/(recall+precision));
        }
        return best;
    }
}
