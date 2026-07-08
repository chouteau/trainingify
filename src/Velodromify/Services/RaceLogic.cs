namespace Velodromify.Services;

public static class RaceLogic
{
    // Standard Velodrome is 250m
    public const double TrackLengthMeters = 250.0;
    
    // Track dimensions (simplified oval)
    // Let's assume a track with two straights and two semi-circles.
    // L = 2*Straight + 2*PI*Radius
    // Let's pick reasonable values. Radius = 25m -> 2*PI*25 = 157m curved part.
    // Remaining = 250 - 157 = 93m. Straight = 46.5m.
    // This is a bit tight. Let's approximate.
    
    // Width of the track view (for normalization)
    public const double TrackWidth = 800;
    public const double TrackHeight = 400;
    
    public const double RadiusX = 150; // Horizontal radius of the curve
    public const double RadiusY = 150; // Vertical radius of the curve (actually it's a circle in 2D usually, but let's make it an oval)
    
    // Let's use a parametric equation for an ellipse/oval.
    // Or better, a stadium shape (rectangle with semi-circles).
    
    // Center of the track
    private const double CenterX = TrackWidth / 2;
    private const double CenterY = TrackHeight / 2;
    
    // Dimensions for the "Stadium" shape
    private const double StraightLength = 300; // Length of the straight section in pixels
    private const double CurveRadius = 100; // Radius of the turn in pixels
    
    // Total visual length in pixels = 2 * StraightLength + 2 * PI * CurveRadius
    // We map 250m to this visual length.
    private const double VisualPerimeter = 2 * StraightLength + 2 * Math.PI * CurveRadius;

    public static (double X, double Y, double Rotation) CalculatePosition(double distanceMeters)
    {
        // Normalize distance to one lap
        double lapDistance = distanceMeters % TrackLengthMeters;
        
        // Map lap distance (0-250) to visual perimeter (0-VisualPerimeter)
        double visualDistance = (lapDistance / TrackLengthMeters) * VisualPerimeter;
        
        // Determine which segment we are on
        // 1. Bottom Straight (going right)
        // 2. Right Curve (going up/left)
        // 3. Top Straight (going left)
        // 4. Left Curve (going down/right)
        
        // Segments:
        // Bottom Straight: 0 to StraightLength
        // Right Curve: StraightLength to StraightLength + PI*R
        // Top Straight: StraightLength + PI*R to 2*StraightLength + PI*R
        // Left Curve: 2*StraightLength + PI*R to End
        
        double segment1 = StraightLength;
        double segment2 = segment1 + Math.PI * CurveRadius;
        double segment3 = segment2 + StraightLength;
        
        double x, y, rotation;
        
        if (visualDistance < segment1)
        {
            // Bottom Straight
            double progress = visualDistance;
            x = CenterX - (StraightLength / 2) + progress;
            y = CenterY + CurveRadius;
            rotation = 0; // Facing Right
        }
        else if (visualDistance < segment2)
        {
            // Right Curve
            double progress = visualDistance - segment1;
            // Angle from -PI/2 to PI/2 (actually we start at -PI/2 which is bottom, going to PI/2 which is top)
            // Wait, standard circle: 0 is right, PI/2 is up.
            // We are at (RightEnd, Bottom). We want to go to (RightEnd, Top).
            // Angle should go from -PI/2 to PI/2.
            double angle = -Math.PI / 2 + (progress / (Math.PI * CurveRadius)) * Math.PI;
            
            x = CenterX + (StraightLength / 2) + CurveRadius * Math.Cos(angle);
            y = CenterY + CurveRadius * Math.Sin(angle);
            rotation = angle + Math.PI / 2; // Tangent
        }
        else if (visualDistance < segment3)
        {
            // Top Straight
            double progress = visualDistance - segment2;
            x = CenterX + (StraightLength / 2) - progress;
            y = CenterY - CurveRadius;
            rotation = Math.PI; // Facing Left
        }
        else
        {
            // Left Curve
            double progress = visualDistance - segment3;
            // Angle from PI/2 to 3PI/2
            double angle = Math.PI / 2 + (progress / (Math.PI * CurveRadius)) * Math.PI;
            
            x = CenterX - (StraightLength / 2) + CurveRadius * Math.Cos(angle);
            y = CenterY + CurveRadius * Math.Sin(angle);
            rotation = angle + Math.PI / 2;
        }

        return (x, y, rotation);
    }
}
