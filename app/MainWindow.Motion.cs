using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using System.Numerics;
namespace LumaMusic;

public sealed partial class MainWindow
{
    int reflectionGeneration;
    async Task UpdateReflection(string? path)
    {
        int request=++reflectionGeneration;ReflectionImage.Source=null;
        if(path==null)return;
        try{var file=await Task.Run(()=>Controls.CoverReflectionRenderer.Render(path));if(request==reflectionGeneration&&!closing&&file!=null)ReflectionImage.Source=new Microsoft.UI.Xaml.Media.Imaging.BitmapImage(new Uri(file));}
        catch(Exception e){Services.AppPaths.Log("Reflection: "+e.Message);}
    }
    string ambientTrackId="";
    int informationFocus=-1;
    bool informationVisible;
    double pullDistance;
    readonly HashSet<string> enteredViews=[];
    void InitializeImmersiveMotion()
    {
        Microsoft.UI.Xaml.Media.Animation.ConnectedAnimationService.GetForCurrentView().DefaultDuration=TimeSpan.FromSeconds(.55);
        LargeCover.ManipulationMode=ManipulationModes.TranslateY;
        LargeCover.ManipulationStarted+=(_,_)=>pullDistance=0;
        LargeCover.ManipulationDelta+=(_,e)=>{
            if(!nowVisible||ReduceMotion)return;
            pullDistance=e.Cumulative.Translation.Y;
            double offset=pullDistance<0?-60*(1-Math.Exp(pullDistance/120)):pullDistance;
            ElementCompositionPreview.SetIsTranslationEnabled(NowColumns,true);
            var ambient=ElementCompositionPreview.GetElementVisual(Ambient);ElementCompositionPreview.SetIsTranslationEnabled(Ambient,true);ambient.Properties.InsertVector3("Translation",new(0,(float)(offset*.3),0));ambient.Opacity=(float)Math.Clamp(1-Math.Max(0,offset)/Math.Max(1,Root.ActualHeight),.25,1);
            ElementCompositionPreview.GetElementVisual(NowColumns).Properties.InsertVector3("Translation",new(0,(float)offset,0));
        };
        LargeCover.ManipulationCompleted+=(_,e)=>{
            if(!nowVisible)return;
            double velocity=e.Velocities.Linear.Y*1000;
            bool dismiss=pullDistance+velocity/1000*.998/(1-.998)>Root.ActualHeight*.25;
            var v=ElementCompositionPreview.GetElementVisual(NowColumns);
            if(ReduceMotion)v.Properties.InsertVector3("Translation",Vector3.Zero);
            else{var a=v.Compositor.CreateSpringVector3Animation();a.FinalValue=Vector3.Zero;a.InitialVelocity=new(0,(float)velocity,0);a.DampingRatio=.8f;a.Period=TimeSpan.FromSeconds(.3);v.StartAnimation("Translation",a);}
            var ambient=ElementCompositionPreview.GetElementVisual(Ambient);ambient.Properties.InsertVector3("Translation",Vector3.Zero);ambient.Opacity=1;
            if(dismiss)ShowLibrary();pullDistance=0;
        };
        Root.Loaded+=(_,_)=>{
            AlbumGrid.ContainerContentChanging+=(_,e)=>EnterItem(e.ItemContainer,e.ItemIndex,"albums",60);
            TrackList.ContainerContentChanging+=(_,e)=>EnterItem(e.ItemContainer,e.ItemIndex,"songs",50);
            UpdateTrackInformation(current);SwitchContent(true);ContentTabs.SizeChanged+=(_,_)=>UpdateContentIndicator();CoverScroll.SizeChanged+=(_,_)=>UpdateResponsiveLayout();
        };
        AlbumsTab.Click+=(_,_)=>EnterBrowse("albums",AlbumGrid);
        SongsTab.Click+=(_,_)=>EnterBrowse("songs",TrackList);
        ArtistsTab.Click+=(_,_)=>EnterBrowse("artists",ArtistList);
    }
    readonly HashSet<string> enteredItems=[];
    void EnterItem(FrameworkElement item,int index,string view,int interval)
    {
        if(item is Control control)Controls.InteractionMotion.AttachItem(control,view=="albums");
        if(index<0||index>24||!enteredItems.Add(view+":"+index))return;
        Entrance(item,index*interval,view=="albums"?600:550,18);
    }
    void EnterBrowse(string key,UIElement view){if(enteredViews.Add(key))Entrance(view,0,400,16);}
    void Entrance(UIElement element,int delay=0,int duration=700,float distance=26,float opacity=1)
    {
        ElementCompositionPreview.SetIsTranslationEnabled(element,true);var v=ElementCompositionPreview.GetElementVisual(element);v.StopAnimation("Translation");v.StopAnimation("Opacity");
        if(ReduceMotion){v.Properties.InsertVector3("Translation",Vector3.Zero);v.Opacity=opacity;return;}
        var c=v.Compositor;var ease=c.CreateCubicBezierEasingFunction(new(.16f,1),new(.3f,1));
        v.Properties.InsertVector3("Translation",new(0,distance,0));v.Opacity=0;
        var slide=c.CreateVector3KeyFrameAnimation();slide.InsertKeyFrame(1,Vector3.Zero,ease);slide.Duration=TimeSpan.FromMilliseconds(duration);slide.DelayTime=TimeSpan.FromMilliseconds(delay);slide.DelayBehavior=AnimationDelayBehavior.SetInitialValueBeforeDelay;
        var fade=c.CreateScalarKeyFrameAnimation();fade.InsertKeyFrame(1,opacity,ease);fade.Duration=slide.Duration;fade.DelayTime=slide.DelayTime;
        v.StartAnimation("Translation",slide);v.StartAnimation("Opacity",fade);
    }
    void EnterNowPlaying()
    {
        InformationScroll.Opacity=informationVisible?1:0;LyricsScroll.Opacity=informationVisible?0:1;
        foreach(var panelHost in new UIElement[]{InformationScroll,LyricsScroll}){var visual=ElementCompositionPreview.GetElementVisual(panelHost);visual.StopAnimation("Opacity");visual.Opacity=panelHost==InformationScroll?(informationVisible?1:0):(informationVisible?0:1);}
        CoverScroll.ChangeView(null,0,null,true);

        // Reflection remains absent while the shared cover is moving.
        Entrance(CoverReflection,550,700,26,.16f);Entrance(NowTitle,380);Entrance(NowArtist,460);Entrance(NowAlbum,540);
        var panel=informationVisible?InformationPanel:LyricsPanel;
        for(int i=0;i<Math.Min(panel.Children.Count,6);i++)Entrance(panel.Children[i],620+i*80);
    }
    void UpdateTrackInformation(Track? track)
    {
        InformationPanel.Children.Clear();informationFocus=-1;
        (string label,string value)[] rows=[("艺人",track?.Artist??"尚未选择"),("专辑",track?.Album??"—"),("音频格式",track?.Quality??"—"),("位深",track?.Bits>0?$"{track.Bits} bit":"—"),("时长",track?.DurationText??"—")];
        foreach(var row in rows){var label=new TextBlock{Text=row.label,FontSize=12,Foreground=AccentBrush()};var value=new TextBlock{Text=row.value,FontSize=15,FontWeight=Microsoft.UI.Text.FontWeights.SemiBold,TextWrapping=TextWrapping.Wrap};var content=new StackPanel{Spacing=6};content.Children.Add(label);content.Children.Add(value);var button=new Button{Content=content,HorizontalAlignment=HorizontalAlignment.Stretch,HorizontalContentAlignment=HorizontalAlignment.Left,Background=new SolidColorBrush(Microsoft.UI.Colors.Transparent),Padding=new(12,4,4,4),BorderThickness=new(2,0,0,0),Opacity=.55};int rowIndex=InformationPanel.Children.Count;button.Click+=(_,_)=>FocusInformation(rowIndex);InformationPanel.Children.Add(button);}
        FocusInformation(0);
    }
    void FocusInformation(int index)
    {
        if(informationFocus==index)return;informationFocus=index;
        for(int i=0;i<InformationPanel.Children.Count;i++){
            var button=(Button)InformationPanel.Children[i];bool focused=i==index;button.Opacity=1;button.BorderBrush=focused?AccentBrush():new SolidColorBrush(Microsoft.UI.Colors.Transparent);
            var content=(StackPanel)button.Content;((TextBlock)content.Children[0]).Foreground=focused?AccentBrush():InactiveLyric();
            var v=ElementCompositionPreview.GetElementVisual(content);float target=focused?1:Math.Abs(i-index)==1?.55f:.30f;
            var value=ElementCompositionPreview.GetElementVisual(content.Children[1]);var size=focused?new Vector3(17f/15,17f/15,1):Vector3.One;
            if(ReduceMotion){v.StopAnimation("Opacity");value.StopAnimation("Scale");v.Opacity=target;value.Scale=size;continue;}
            var fade=v.Compositor.CreateScalarKeyFrameAnimation();fade.InsertKeyFrame(1,target);fade.Duration=TimeSpan.FromSeconds(.55);v.StartAnimation("Opacity",fade);
            var scale=v.Compositor.CreateVector3KeyFrameAnimation();scale.InsertKeyFrame(1,size);scale.Duration=fade.Duration;value.StartAnimation("Scale",scale);
        }
    }
    void InfoView_Click(object sender,RoutedEventArgs e)=>SwitchContent(true);
    void LyricsView_Click(object sender,RoutedEventArgs e)=>SwitchContent(false);
    void SwitchContent(bool information)
    {
        if(informationVisible==information)return;informationVisible=information;
        InformationScroll.IsHitTestVisible=information;LyricsScroll.IsHitTestVisible=!information;
        if(information)InformationScroll.Opacity=1;else LyricsScroll.Opacity=1;
        var outgoing=information?(UIElement)LyricsScroll:InformationScroll;var incoming=information?(UIElement)InformationScroll:LyricsScroll;
        ElementCompositionPreview.SetIsTranslationEnabled(outgoing,true);var old=ElementCompositionPreview.GetElementVisual(outgoing);
        var lift=old.Compositor.CreateVector3KeyFrameAnimation();lift.InsertKeyFrame(1,new(0,ReduceMotion?0:-14,0));lift.Duration=TimeSpan.FromSeconds(.28);if(!ReduceMotion)old.StartAnimation("Translation",lift);
        Fade(outgoing,false);Entrance(incoming,120,550,0);Fade(LyricsPlaceholder,!information);
        UpdateContentIndicator();
        InfoViewButton.Background=information?SelectionBrush():new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        LyricsViewButton.Background=!information?SelectionBrush():new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        var panel=information?InformationPanel:LyricsPanel;
        // Entrance belongs to the row wrapper; lyric focus belongs to its text.
        for(int i=0;i<Math.Min(panel.Children.Count,16);i++)Entrance(panel.Children[i],120+i*45,550,18);
        UpdateLyricFocus(lyricIndex);
    }
    void UpdateContentIndicator()
    {
        var button=informationVisible?InfoViewButton:LyricsViewButton;var position=button.TransformToVisual(ContentTabs).TransformPoint(new());
        ContentSelection.Width=button.ActualWidth;ElementCompositionPreview.SetIsTranslationEnabled(ContentSelection,true);var v=ElementCompositionPreview.GetElementVisual(ContentSelection);var target=new Vector3((float)position.X,0,0);
        if(ReduceMotion){v.StopAnimation("Translation");v.Properties.InsertVector3("Translation",target);return;}
        var a=v.Compositor.CreateSpringVector3Animation();a.FinalValue=target;a.DampingRatio=1;a.Period=TimeSpan.FromSeconds(.45);v.StartAnimation("Translation",a);
    }
    void UpdateLyricFocus(int index)
    {
        for(int i=0;i<lyricButtons.Count;i++){
            var text=(TextBlock)lyricButtons[i].Content;var v=ElementCompositionPreview.GetElementVisual(text);ElementCompositionPreview.SetIsTranslationEnabled(text,true);
            float opacity=(float)LyricOpacity(i,index);var target=i==index?new Vector3(1.04f,1.04f,1):Vector3.One;
            text.Foreground=i==index?ActiveLyric():InactiveLyric();lyricButtons[i].Opacity=1;
            if(ReduceMotion){v.StopAnimation("Opacity");v.StopAnimation("Scale");v.StopAnimation("Translation");v.Opacity=opacity;v.Scale=Vector3.One;v.Properties.InsertVector3("Translation",Vector3.Zero);continue;}
            var c=v.Compositor;var ease=c.CreateCubicBezierEasingFunction(new(.16f,1),new(.3f,1));
            var scale=c.CreateVector3KeyFrameAnimation();scale.InsertKeyFrame(1,target,ease);scale.Duration=TimeSpan.FromSeconds(.55);v.StartAnimation("Scale",scale);
            var move=c.CreateVector3KeyFrameAnimation();move.InsertKeyFrame(1,new(i==index?7:0,0,0),ease);move.Duration=scale.Duration;v.StartAnimation("Translation",move);
            var fade=c.CreateScalarKeyFrameAnimation();fade.InsertKeyFrame(1,opacity,ease);fade.Duration=scale.Duration;v.StartAnimation("Opacity",fade);
        }
    }
}
