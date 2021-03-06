module FixLib

open Fake
open Fake.Git
open Fake.FileHelper
open Fix.ProjectSystem
open System.IO
open System
open System.Diagnostics
open System.Net

let (^) = (<|)

let exeLocation = System.Reflection.Assembly.GetEntryAssembly().Location |> Path.GetDirectoryName
let templatesLocation = exeLocation </> "templates"
let paketTemplate = templatesLocation </> ".paket"
let directory = System.Environment.CurrentDirectory
let packagesDirectory = directory </> "packages"

let paketLocation = exeLocation </> "Tools" </> "Paket"
let fakeLocation = exeLocation </> "Tools" </> "FAKE"
let fakeToolLocation = fakeLocation </> "tools"

let RefreshTemplates () =
    printfn "Getting templates..."
    Path.Combine(exeLocation, "templates") |> Fake.FileHelper.CleanDir
    Repository.cloneSingleBranch exeLocation "https://github.com/fsprojects/generator-fsharp.git" "templates" "templates"

let applicationNameToProjectName folder projectName =
    let applicationName = "ApplicationName"
    let files = Directory.GetFiles folder |> Seq.where (fun x -> x.Contains applicationName)
    files |> Seq.iter (fun x -> File.Copy(x, x.Replace(applicationName, projectName)))

let copyPaket folder =
    folder </> ".paket" |> Directory.CreateDirectory |> ignore
    Directory.GetFiles paketTemplate
    |> Seq.iter (fun x ->
        let fn = Path.GetFileName x
        File.Copy (x, folder </> ".paket" </> fn) )


let sed (find:string) replace folder =
    folder
    |> Directory.GetFiles
    |> Seq.iter (fun x ->
                    let r = replace x
                    let contents = File.ReadAllText(x).Replace(find, r)
                    File.WriteAllText(x, contents))

let relative (path1 : string) (path2 : string) =
    let p1 = Uri(path1)
    let p2 = Uri(path2)
    Uri.UnescapeDataString(
        p2.MakeRelativeUri(p1)
          .ToString()
          .Replace('/', Path.DirectorySeparatorChar)
    )

let promptProjectName () =
    printfn "Give project name:"
    Console.Write("> ")
    Console.ReadLine()

let promptProjectDir () =
    printfn "Give project directory (relative to working directory):"
    Console.Write("> ")
    Console.ReadLine()

let promptList () =
    printfn "Choose a template:"
    let templates = Directory.GetDirectories(templatesLocation)
                    |> Seq.map Path.GetFileName
                    |> Seq.where (fun x -> not <| x.StartsWith("."))
    templates |> Seq.iter (fun x -> printfn " - %s" x)
    printfn ""
    Console.Write("> ")
    Console.ReadLine()


let alterProject project (f : ProjectFile -> ProjectFile) =
    let fsProj = ProjectFile.FromFile(project)
    let updatedProject = fsProj |> f
    updatedProject.Save(project)

let nodeType fileName =
    match Path.GetExtension fileName with
    | ".fs" -> "Compile"
    | ".config" | ".html"-> "Content"
    | _ -> "None"

let addFileToProject fileName project nodeType = alterProject project (fun x -> x.AddFile fileName nodeType)
let removeFileFromProject fileName project _ = alterProject project (fun x -> x.RemoveFile fileName)
let addReferenceToProject reference project = alterProject project (fun x -> x.AddReference reference)

let file fileName f =
    let projects = DirectoryInfo(directory) |> Fake.FileSystemHelper.filesInDirMatching "*.fsproj"
    let node = nodeType fileName
    match projects with
    | [| project |] -> f fileName project.Name node
    | [||] -> printfn "No project found in this directory."
    | _ ->
        let project = promptList ()
        f fileName project node

let Add fileName =
    file fileName addFileToProject
    Path.Combine(directory, fileName) |> Fake.FileHelper.CreateFile

let AddReference reference =
    let projects = DirectoryInfo(directory) |> Fake.FileSystemHelper.filesInDirMatching "*.fsproj"
    match projects with
    | [| project |] -> addReferenceToProject reference project.Name
    | [||] -> printfn "No project found in this directory."
    | _ ->
        let project = promptList ()
        addReferenceToProject reference project

let Remove fileName =
    file fileName removeFileFromProject
    Path.Combine(directory, fileName) |> Fake.FileHelper.DeleteFile

let run cmd args dir =
    if execProcess( fun info ->
        info.FileName <- cmd
        if not( String.IsNullOrWhiteSpace dir) then
            info.WorkingDirectory <- dir
        info.Arguments <- args
    ) System.TimeSpan.MaxValue = false then
        traceError <| sprintf "Error while running '%s' with args: %s" cmd args

let UpdatePaket () =
    run (paketLocation </> "paket.bootstrapper.exe") "" paketLocation

let RunPaket args =
    let f = paketLocation </> "paket.exe"
    if not ^ File.Exists f then UpdatePaket ()
    let args' = args |> String.concat " "
    run f args' directory

let UpdateFake () =
    use wc = new WebClient()
    let zip = fakeLocation </> "fake.zip"
    System.IO.Directory.CreateDirectory(fakeLocation) |> ignore
    printfn "Downloading FAKE..."
    wc.DownloadFile("https://www.nuget.org/api/v2/package/FAKE", zip )
    Fake.ZipHelper.Unzip fakeLocation zip

let RunFake args =
    let f = fakeToolLocation </> "FAKE.exe"
    if not ^ File.Exists f then UpdateFake ()
    let args' = args |> String.concat " "
    run f args' directory

let New projectName projectDir templateName paket =
    if not ^ Directory.Exists templatesLocation then RefreshTemplates ()

    let projectName' = if String.IsNullOrWhiteSpace projectName then promptProjectName () else projectName
    let projectDir' = if String.IsNullOrWhiteSpace projectDir then promptProjectDir () else projectDir
    let templateName' = if String.IsNullOrWhiteSpace templateName then promptList () else templateName
    let projectFolder = directory </> projectDir' </> projectName'
    let templateDir = templatesLocation </> templateName'

    printfn "Generating project..."

    Fake.FileHelper.CopyDir projectFolder templateDir (fun _ -> true)
    applicationNameToProjectName projectFolder projectName'

    sed "<%= namespace %>" (fun _ -> projectName') projectFolder
    sed "<%= guid %>" (fun _ -> Guid.NewGuid().ToString()) projectFolder
    sed "<%= paketPath %>" (relative directory) projectFolder
    sed "<%= packagesPath %>" (relative packagesDirectory) projectFolder
    if paket then
        copyPaket directory
        RunPaket ["convert-from-nuget";"-f"]
    printfn "Done!"

let Help () =
    printfn"Fix (Mix for F#)\n\
            Available Commands:\n\n\
            new [projectName] [projectDir] [templateName] [--no-paket] - Creates a new project\
          \n                      with the given name, in given directory\
          \n                      (relative to working directory) and given template.\
          \n                      If parameters are not provided, program prompts user for them\n\
            file add [fileName] - Adds a file to the current folder and project.\
          \n                      If more than one project is in the current\
          \n                      directory you will be prompted which to use.\n\
            file remove [fileName]\
          \n                    - Removes the filename from disk and the project.\
          \n                      If more than one project is in the current\
          \n                      directory you will be prompted which to use.\n\
            reference add [reference]\
          \n                    - Add reference to the current project.\
          \n                      If more than one project is in the current\
          \n                      directory you will be prompted which to use.\n\
            update paket        - Updates Paket to latest version\n\
            update fake         - Updates FAKE to latest version\n\
            paket [args]        - Runs Paket with given arguments\n\
            fake [args]         - Runs FAKE with given arguments\n\
            refresh             - Refreshes the template cache\n\
            help                - Displays this help\n\
            exit                - Exit interactive mode\n"


let rec consoleLoop f =
    Console.Write("> ")
    let input = Console.ReadLine()
    let result = input.Split(' ') |> Array.toList |> f
    if result > 0
    then result
    else consoleLoop f

//TODO: Better input handling, maybe Argu ?
let handleInput = function
    | [ "new" ] -> New "" "" "" true; 1
    | [ "new"; "--no-paket" ] -> New "" "" "" false; 1
    | [ "new"; projectName ] -> New projectName "" "" true; 1
    | [ "new"; projectName; "--no-paket"] -> New projectName "" "" false; 1
    | [ "new"; projectName; projectDir ] -> New projectName projectDir "" true; 1
    | [ "new"; projectName; projectDir; "--no-paket" ] -> New projectName projectDir "" false; 1
    | [ "new"; projectName; projectDir; templateName ] -> New projectName projectDir templateName true; 1
    | [ "new"; projectName; projectDir; templateName; "--no-paket" ] -> New projectName projectDir templateName false; 1
    | [ "file"; "add"; fileName ] -> Add fileName; 0
    | [ "file"; "remove"; fileName ] -> Remove fileName; 0
    | [ "reference"; "add"; fileName ] -> AddReference fileName; 0
    | [ "update"; "paket"] -> UpdatePaket (); 0
    | [ "update"; "fake"] -> UpdateFake (); 0
    | "paket"::xs -> RunPaket xs; 0
    | "fake"::xs -> RunFake xs; 0
    | [ "refresh" ] -> RefreshTemplates (); 0
    | [ "exit" ] -> 1
    | _ -> Help(); 0


[<EntryPoint>]
let main argv =
    if argv |> Array.isEmpty
    then
        Help ()
        consoleLoop handleInput
    else handleInput (argv |> Array.toList)
