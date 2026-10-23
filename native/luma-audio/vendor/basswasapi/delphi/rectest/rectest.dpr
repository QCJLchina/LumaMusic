program rectest;

uses
  Forms,
  MainFrm in 'MainFrm.pas' {Form1},
  bass in '..\bass.pas',
  bassmix in '..\bassmix.pas',
  basswasapi in '..\basswasapi.pas';

{$R *.res}

begin
  Application.Initialize;
  Application.MainFormOnTaskbar := True;
  Application.CreateForm(TfrmWASAPImicRec, frmWASAPImicRec);
  Application.Run;
end.
