using CAMAPI.DotnetHelper;
using CAMIPC.Application;
using ProjectTestingUtility.Services;

var form = new FakeMainForm();
using var app = new ComWrapper<ICamIpcApplication>(new FakeApplication { MainForm = form });
var missingWindow = CamWindowActivation.Activate(app, @"C:\check\2097.stcp");
Require(form.Requested && missingWindow.SdkActivationRequested, "The exact connected backend must receive the SDK activation request.");
Require(!missingWindow.IsForeground && !missingWindow.IsVisible && missingWindow.WindowHandle == 0 && missingWindow.Warning != null,
    "An absent target window must not result in selecting an unrelated process window.");
Console.WriteLine("PASS missing owned window returns a diagnostic warning without activating any other process");

using var rejected = new ComWrapper<ICamIpcApplication>(new FakeApplication { MainForm = new FakeMainForm { Reject = true } });
Require(CamWindowActivation.Activate(rejected).Warning?.Contains("Synthetic SDK failure") == true,
    "Activation failure must be reported without failing a project test.");
Console.WriteLine("PASS SDK activation errors are non-fatal");

Require(CamWindowActivation.MatchesProjectLabel("2097", @"C:\check\2097.stcp"), "Project stem should match.");
Require(CamWindowActivation.MatchesProjectLabel("2097.stcp*", @"C:\check\2097.stcp"), "Unsaved project tab should match.");
Require(!CamWindowActivation.MatchesProjectLabel("12097", @"C:\check\2097.stcp"), "Project labels must not match substrings.");
Require(!CamWindowActivation.MatchesProjectLabel("Home", @"C:\check\2097.stcp"), "Home must never match a project tab.");
Console.WriteLine("PASS exact project label matching rejects unrelated and Home tabs");
Console.WriteLine("All activation checks passed. No visible window or ENCY process was created or activated.");

static void Require(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}
