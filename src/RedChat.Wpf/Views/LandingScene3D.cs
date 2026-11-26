using System.Windows;
using System.Windows.Controls;
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

        GeometryModel3D outerOrbit = CreateTorus(1.68, 0.014, Color.FromArgb(205, 255, 91, 123));
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

        AddSatellite(scene, new Point3D(-1.04, 0.62, 0.42), 0.105, Color.FromRgb(255, 112, 137), 0.12, 2.8);
        AddSatellite(scene, new Point3D(1.10, -0.58, 0.22), 0.075, Color.FromRgb(128, 172, 245), 0.09, 3.4);
        AddSatellite(scene, new Point3D(0.12, 1.20, -0.35), 0.06, Color.FromRgb(255, 190, 130), 0.15, 2.5);

        ModelVisual3D visual = new() { Content = scene };
        viewport.Children.Add(visual);
    }

    private static void AddSatellite(Model3DGroup scene, Point3D center, double radius, Color color, double bobAmount, double durationSeconds)
    {
        GeometryModel3D satellite = CreateSphere(radius, color,
            Color.FromRgb((byte)(color.R / 3), (byte)(color.G / 3), (byte)(color.B / 3)));
        ScaleTransform3D scale = new(1, 1, 1);
        TranslateTransform3D bob = new(center.X, center.Y, center.Z);
        Transform3DGroup transform = new();
        transform.Children.Add(scale);
        transform.Children.Add(bob);
        satellite.Transform = transform;
        bob.BeginAnimation(
            TranslateTransform3D.OffsetYProperty,
            new DoubleAnimation(center.Y - bobAmount, center.Y + bobAmount, TimeSpan.FromSeconds(durationSeconds))
            {
                AutoReverse = true,
                RepeatBehavior = RepeatBehavior.Forever,
                EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut },
            });
        scale.BeginAnimation(
            ScaleTransform3D.ScaleXProperty,
            new DoubleAnimation(0.88, 1.12, TimeSpan.FromSeconds(durationSeconds * 1.15))
            {
                AutoReverse = true,
                RepeatBehavior = RepeatBehavior.Forever,
                EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut },
            });
        scale.BeginAnimation(
            ScaleTransform3D.ScaleYProperty,
            new DoubleAnimation(0.88, 1.12, TimeSpan.FromSeconds(durationSeconds * 1.15))
            {
                AutoReverse = true,
                RepeatBehavior = RepeatBehavior.Forever,
                EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut },
            });
        scale.BeginAnimation(
            ScaleTransform3D.ScaleZProperty,
            new DoubleAnimation(0.88, 1.12, TimeSpan.FromSeconds(durationSeconds * 1.15))
            {
                AutoReverse = true,
                RepeatBehavior = RepeatBehavior.Forever,
                EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut },
            });
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
