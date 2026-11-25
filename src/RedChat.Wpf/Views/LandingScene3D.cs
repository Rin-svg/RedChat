using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Media3D;
using Color = System.Windows.Media.Color;
using Point = System.Windows.Point;

namespace RedChat.Wpf.Views;

internal static class LandingScene3D
{
    public static void Initialize(Viewport3D viewport)
    {
        ArgumentNullException.ThrowIfNull(viewport);
        viewport.Camera = new PerspectiveCamera(
            new Point3D(0, 0, 6),
            new Vector3D(0, 0, -6),
            new Vector3D(0, 1, 0),
            42);

        Model3DGroup scene = new();
        scene.Children.Add(new AmbientLight(Color.FromRgb(105, 110, 132)));
        scene.Children.Add(new DirectionalLight(Colors.White, new Vector3D(-1, -1, -2)));
        scene.Children.Add(new DirectionalLight(Color.FromRgb(255, 74, 106), new Vector3D(1, 0.4, 1)));

        scene.Children.Add(CreateLogoModel());

        GeometryModel3D outerOrbit = CreateTorus(1.42, 0.018, Color.FromArgb(225, 255, 91, 123));
        AxisAngleRotation3D orbitRotation = new(new Vector3D(0.9, 0.35, 0.2), -28);
        outerOrbit.Transform = new RotateTransform3D(orbitRotation);
        orbitRotation.BeginAnimation(
            AxisAngleRotation3D.AngleProperty,
            new DoubleAnimation(-28, 332, TimeSpan.FromSeconds(34))
            {
                RepeatBehavior = RepeatBehavior.Forever,
                IsCumulative = true,
            });
        scene.Children.Add(outerOrbit);

        GeometryModel3D innerOrbit = CreateTorus(1.22, 0.009, Color.FromArgb(170, 114, 163, 232));
        AxisAngleRotation3D innerRotation = new(new Vector3D(0.1, 1, 0.55), 44);
        innerOrbit.Transform = new RotateTransform3D(innerRotation);
        innerRotation.BeginAnimation(
            AxisAngleRotation3D.AngleProperty,
            new DoubleAnimation(44, -316, TimeSpan.FromSeconds(42))
            {
                RepeatBehavior = RepeatBehavior.Forever,
                IsCumulative = true,
            });
        scene.Children.Add(innerOrbit);

        AddSatellite(scene, new Point3D(-1.04, 0.62, 0.42), 0.105, Color.FromRgb(255, 112, 137));
        AddSatellite(scene, new Point3D(1.10, -0.58, 0.22), 0.075, Color.FromRgb(128, 172, 245));
        AddSatellite(scene, new Point3D(0.12, 1.20, -0.35), 0.06, Color.FromRgb(255, 190, 130));

        ModelVisual3D visual = new() { Content = scene };
        viewport.Children.Add(visual);
    }

    private static Model3DGroup CreateLogoModel()
    {
        const double width = 2.95;
        const double height = 1.72;
        const double depth = 0.055;

        MeshGeometry3D backingMesh = CreatePlane(width + 0.12, height + 0.12, -depth);
        MaterialGroup backingMaterial = new();
        backingMaterial.Children.Add(new DiffuseMaterial(new SolidColorBrush(Color.FromRgb(119, 10, 39))));
        backingMaterial.Children.Add(new EmissiveMaterial(new SolidColorBrush(Color.FromRgb(73, 7, 30))));
        GeometryModel3D backing = new(backingMesh, backingMaterial) { BackMaterial = backingMaterial };

        MeshGeometry3D faceMesh = CreatePlane(width, height, 0);
        DrawingVisual drawing = new();
        using (DrawingContext context = drawing.RenderOpen())
        {
            context.PushTransform(new ScaleTransform(5, 5));
            context.DrawGeometry(
                new SolidColorBrush(Color.FromRgb(242, 29, 69)), null,
                CreateLogoPath("M 7,23 C 10,12 20,5 32,6 C 39,7 44,12 46,18 C 39,16 31,17 24,20 C 17,23 12,25 7,23 Z"));
            context.DrawGeometry(
                new SolidColorBrush(Color.FromRgb(255, 120, 144)), null,
                CreateLogoPath("M 4,24 C 12,25 19,22 26,19 C 34,16 42,16 49,19 C 45,24 35,27 25,28 C 15,29 7,28 4,24 Z"));
            context.DrawGeometry(
                new SolidColorBrush(Color.FromRgb(169, 14, 44)), null,
                CreateLogoPath("M 8,29 C 19,31 35,29 45,25 C 41,31 27,34 15,32 C 12,32 9,31 8,29 Z"));
            context.Pop();
        }

        RenderTargetBitmap bitmap = new(300, 180, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(drawing);
        bitmap.Freeze();
        ImageBrush logoTexture = new(bitmap)
        {
            Stretch = Stretch.Fill,
            ViewportUnits = BrushMappingMode.RelativeToBoundingBox,
        };
        MaterialGroup faceMaterial = new();
        faceMaterial.Children.Add(new DiffuseMaterial(logoTexture));
        faceMaterial.Children.Add(new SpecularMaterial(
            new SolidColorBrush(Color.FromArgb(110, 255, 223, 231)), 32));
        GeometryModel3D face = new(faceMesh, faceMaterial) { BackMaterial = faceMaterial };

        Model3DGroup emblem = new();
        emblem.Children.Add(backing);
        emblem.Children.Add(face);

        AxisAngleRotation3D tilt = new(new Vector3D(0, 1, 0), -12);
        RotateTransform3D swivel = new(tilt);
        TranslateTransform3D floatTransform = new();
        Transform3DGroup transform = new();
        transform.Children.Add(swivel);
        transform.Children.Add(floatTransform);
        emblem.Transform = transform;
        tilt.BeginAnimation(
            AxisAngleRotation3D.AngleProperty,
            new DoubleAnimation(-12, 12, TimeSpan.FromSeconds(5.5))
            {
                AutoReverse = true,
                RepeatBehavior = RepeatBehavior.Forever,
            });
        floatTransform.BeginAnimation(
            TranslateTransform3D.OffsetYProperty,
            new DoubleAnimation(-0.06, 0.06, TimeSpan.FromSeconds(3.2))
            {
                AutoReverse = true,
                RepeatBehavior = RepeatBehavior.Forever,
            });

        return emblem;
    }

    private static Geometry CreateLogoPath(string path)
    {
        Geometry geometry = Geometry.Parse(path);
        geometry.Transform = new TranslateTransform(7, 1);
        return geometry;
    }

    private static MeshGeometry3D CreatePlane(double width, double height, double z)
    {
        double halfWidth = width / 2;
        double halfHeight = height / 2;
        MeshGeometry3D mesh = new()
        {
            Positions = new Point3DCollection
            {
                new(-halfWidth, halfHeight, z),
                new(-halfWidth, -halfHeight, z),
                new(halfWidth, halfHeight, z),
                new(halfWidth, -halfHeight, z),
            },
            Normals = new Vector3DCollection
            {
                new(0, 0, 1), new(0, 0, 1), new(0, 0, 1), new(0, 0, 1),
            },
            TextureCoordinates = new PointCollection
            {
                new(0, 0), new(0, 1), new(1, 0), new(1, 1),
            },
            TriangleIndices = new Int32Collection { 0, 1, 2, 2, 1, 3 },
        };
        return mesh;
    }

    private static void AddSatellite(Model3DGroup scene, Point3D center, double radius, Color color)
    {
        GeometryModel3D satellite = CreateSphere(radius, color,
            Color.FromRgb((byte)(color.R / 3), (byte)(color.G / 3), (byte)(color.B / 3)));
        satellite.Transform = new TranslateTransform3D(center.X, center.Y, center.Z);
        scene.Children.Add(satellite);
    }

    private static GeometryModel3D CreateSphere(double radius, Color diffuseColor, Color emissiveColor)
    {
        const int slices = 40;
        const int stacks = 28;
        MeshGeometry3D mesh = new();

        for (int stack = 0; stack <= stacks; stack++)
        {
            double latitude = Math.PI * stack / stacks;
            double y = Math.Cos(latitude);
            double ring = Math.Sin(latitude);
            for (int slice = 0; slice <= slices; slice++)
            {
                double longitude = 2 * Math.PI * slice / slices;
                Vector3D normal = new(ring * Math.Cos(longitude), y, ring * Math.Sin(longitude));
                mesh.Positions.Add(new Point3D(normal.X * radius, normal.Y * radius, normal.Z * radius));
                mesh.Normals.Add(normal);
                mesh.TextureCoordinates.Add(new Point((double)slice / slices, (double)stack / stacks));
            }
        }

        for (int stack = 0; stack < stacks; stack++)
        {
            for (int slice = 0; slice < slices; slice++)
            {
                int current = stack * (slices + 1) + slice;
                int next = current + slices + 1;
                mesh.TriangleIndices.Add(current);
                mesh.TriangleIndices.Add(current + 1);
                mesh.TriangleIndices.Add(next);
                mesh.TriangleIndices.Add(current + 1);
                mesh.TriangleIndices.Add(next + 1);
                mesh.TriangleIndices.Add(next);
            }
        }

        MaterialGroup material = new();
        material.Children.Add(new DiffuseMaterial(new SolidColorBrush(diffuseColor)));
        material.Children.Add(new EmissiveMaterial(new SolidColorBrush(emissiveColor)));
        material.Children.Add(new SpecularMaterial(new SolidColorBrush(Color.FromArgb(170, 255, 224, 231)), 48));
        return new GeometryModel3D(mesh, material) { BackMaterial = material };
    }

    private static GeometryModel3D CreateTorus(double majorRadius, double tubeRadius, Color color)
    {
        const int majorSegments = 96;
        const int minorSegments = 10;
        MeshGeometry3D mesh = new();
        for (int major = 0; major <= majorSegments; major++)
        {
            double u = 2 * Math.PI * major / majorSegments;
            double cosU = Math.Cos(u);
            double sinU = Math.Sin(u);
            for (int minor = 0; minor <= minorSegments; minor++)
            {
                double v = 2 * Math.PI * minor / minorSegments;
                double cosV = Math.Cos(v);
                double sinV = Math.Sin(v);
                double ring = majorRadius + tubeRadius * cosV;
                mesh.Positions.Add(new Point3D(ring * cosU, ring * sinU, tubeRadius * sinV));
                mesh.Normals.Add(new Vector3D(cosU * cosV, sinU * cosV, sinV));
                mesh.TextureCoordinates.Add(new Point((double)major / majorSegments, (double)minor / minorSegments));
            }
        }

        for (int major = 0; major < majorSegments; major++)
        {
            for (int minor = 0; minor < minorSegments; minor++)
            {
                int current = major * (minorSegments + 1) + minor;
                int next = current + minorSegments + 1;
                mesh.TriangleIndices.Add(current);
                mesh.TriangleIndices.Add(next);
                mesh.TriangleIndices.Add(current + 1);
                mesh.TriangleIndices.Add(current + 1);
                mesh.TriangleIndices.Add(next);
                mesh.TriangleIndices.Add(next + 1);
            }
        }

        MaterialGroup material = new();
        material.Children.Add(new DiffuseMaterial(new SolidColorBrush(color)));
        material.Children.Add(new EmissiveMaterial(new SolidColorBrush(Color.FromArgb(
            color.A, (byte)(color.R / 2), (byte)(color.G / 2), (byte)(color.B / 2)))));
        return new GeometryModel3D(mesh, material) { BackMaterial = material };
    }
}
