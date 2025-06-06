﻿module Editor

(*
    This module implements a simple code editor with syntax highlighting and error indication.
    The editor is implemented using the Elmish framework.
    The editor is scrollable in both X and Y directions.
    The text is color highlighted using a simple highlighter written in F# using partial active patterns
    to match each limne of text.
    In addition, the editor supports an error indication underlining text.

    This is currently a simple implementation that does not support
    all the features of a full code editor. In particular, it does not support
    arrow keys, delete, or code selection, cut, and paste.
    These features could be added here in the future if needed.

    To make performance acceptable with large code files, the editor is implemented using React memoisation.

    The editor uses one field in the Issie model to hold the editor state.

*)

open EEExtensions
open Fulma
open Fable.React
open Fable.React.Props
open CommonTypes
open ModelType
open Elmish

/// constants used by code editor
module Constants =
    let leftMargin = 100.0 // left margin for code editor text
    let leftGutter = 20.0 // distance between code numbers and text

/// Initial state of the code editor.
let initCodeEditorState: CodeEditorModel =
    { Lines = [ "" ] // code must have at least one (possibly zero length) line.
      Errors = []
      CursorPos = { X = 0; Y = 0 } }

/// Memoizes the function as a react component so that it is not called unless the props change.
/// In addition, if the function is not called, the React DOM from the function result is not updated.
/// This is important for performance when the react DOM is large.
let reactMemoize (functionToMemoize: 'Props -> ReactElement) (name: string) (key: ('Props -> string) option) (props: 'Props) =
    match key with
    | None ->
        FunctionComponent.Of (functionToMemoize, displayName = name, memoizeWith = Fable.React.Helpers.equalsButFunctions) props
    | Some key ->
        FunctionComponent.Of
            (functionToMemoize, displayName = name, memoizeWith = Fable.React.Helpers.equalsButFunctions, withKey = key)
            props

/// Returns the intersection of two intervals each defining the end points
/// of a raster scan (ascending order X first then Y), or None if there is no intersection.
let intersectionOpt (other: Interval) (this: Interval) =
    let startPos =
        match System.Math.Sign(this.Start.Y - other.Start.Y) with
        | 0 -> // same line
            { X = max this.Start.X other.Start.X; Y = this.Start.Y }
        | -1 -> other.Start
        | _ -> this.Start
    let endPos =
        match System.Math.Sign(this.End.Y - other.End.Y) with
        | 0 -> // same line
            { X = min this.End.X other.End.X; Y = this.End.Y }
        | -1 -> this.End
        | _ -> other.End
    if
        startPos.Y > endPos.Y
        || (startPos.Y = endPos.Y && startPos.X > endPos.X)
    then
        None
    else
        //printfn $"Intersecting {this} with {other} gives {startPos} to {endPos}"
        Some { Start = startPos; End = endPos }

//--------------------------------------------------------------------------------//
//---------------Types and Active Patterns for parsing and highlighting-----------//
//--------------------------------------------------------------------------------//

/// the type of the code editor's text string asociated with a given highlight color
type HighlightT =
    | Normal
    | Keyword
    | Identifier
    | Number
    | String

/// The type of the input to the highlighter.
/// This is a list of characters, since the input to the highlighter is a string.
/// Matching operations have type: MatchStream -> (string * ElementType) * MatchStream.
/// TODO:
/// MatchStream could have state added to make the highlighting context-sensitive.
/// E.g. to remember if the string being highlighted starts in a comment or not: MatchStream = bool * char list.
/// More generally Matchstream = StateT * char list where StateT is a discriminated union.
type MatchStream = char list

/// Return true if the characters in str, interpreted as a string, start with prefix.
let charsStartWith (prefix: string) (str: char list) : bool =
    prefix.Length <= str.Length
    && prefix
       |> Seq.indexed
       |> Seq.forall (fun (index, c) -> str[index] = c)

/// Active pattern helper that matches if a list of chars starts with startP.
/// If it matches the return value is a pair:
/// the first character and the string of characters matching inMatchP
/// the list of chars from the first character that does not match inMatchP.
/// or None if the first character does not match startP.
/// NB - this cannot itself be an active pattern since startP and inMatchP are not literals.
let makeAPMatch (startP: char -> bool) (inMatchP: char -> bool) (str: MatchStream) : (string * MatchStream) option =
    match str with
    | [] -> None
    | start :: rest when startP start ->
        let unMatchedPart = List.skipWhile inMatchP rest
        let matchedString =
            str[0 .. str.Length - unMatchedPart.Length - 1]
            |> System.String.Concat
        Some(matchedString, unMatchedPart)
    | _ -> None

/// Returns as many characters not part of any other active pattern match as possible.
/// This must be adjusted to match the otehr active patterns.
/// Note that doing this without a this pattern, using a default for each character, would be
/// inefficient since it would require a match for each character in the string. In addition it would
/// require another pass to common up sequences of characters.
/// However that would make the code more robust, and perhaps it would be better to do this.
let (|NormalMatch|_|) (str: MatchStream) : (string * MatchStream) option =
    let isNormal c =
        c <> '"' && not (System.Char.IsLetterOrDigit c)
    makeAPMatch isNormal isNormal str

/// Active Pattern that matches if a char list starts with any of the prefixes in a list.
/// Returns the prefix and the remaining characters if it does.
/// NB - prefixL must be a literal for this to work
let (|StringLP|_|) (prefixL: string list) (str: MatchStream) : (string * MatchStream) option =
    prefixL
    |> List.tryPick (fun prefix ->
        if charsStartWith prefix str then
            Some(prefix, str.[prefix.Length ..])
        else
            None)

/// Active Pattern that matches if str starts with a given character.
/// Returns the remaining string if it does.
/// NB - charToMatch must be a literal for this to work
let (|CharP|_|) (charToMatch: char) (str: MatchStream) : MatchStream option =
    if str.Length > 0 && str[0] = charToMatch then
        Some str.[1..]
    else
        None

/// Active Pattern that matches if str starts with a string literal.
/// A string literal is a string starting with a quote and ending with a quote.
/// Returns the non quote part of the string literal and the remainder of str if it does.
/// The closing quote is not removed from str.
let (|StringLiteralStart|_|) (str: MatchStream) : (string * MatchStream) option =
    // TODO: this should be improved to handle escaped quotes.
    // TODO: this should be improved to handle multi-line strings, that would require
    // context-sensitive matching of each editor line - where tyhe context is a boolean
    // indicating if the string is inside a multi-line string or not.
    let isStringStart c = c = '"'
    let isStringPart c = c <> '"'
    makeAPMatch isStringStart isStringPart str

/// Active Pattern that matches if str starts with an identifier.
/// Returns the identifier and the remaining string if it does.
let (|IdentifierP|_|) (str: MatchStream) : (string * MatchStream) option =
    let isIdentifierStart c = System.Char.IsLetter c || c = '_'
    let isInsideIdentifier c =
        System.Char.IsLetterOrDigit c || c = '_'
    makeAPMatch isIdentifierStart isInsideIdentifier str

/// Active Pattern that matches if str starts with a number.
/// Returns the number and the remaining string if it does.
let (|NumberP|_|) (str: MatchStream) : (string * MatchStream) option =
    let isNumberStart c = System.Char.IsDigit c
    let isInsideNumber c = System.Char.IsDigit c
    makeAPMatch isNumberStart isInsideNumber str

/// Return chars segmented as a list of strings each paired with a HighlightT.
/// The string is the text to be highlighted and the HighlightT determines the color.
/// HighlightT to color mapping is done in the render function.
let rec highlight (chars: MatchStream) : (string * HighlightT) list =
    match chars with
    | [] -> [] // finish
    | StringLP [ "let"; "if"; "then"; "else" ] (keyword, rest) ->
        (keyword, Keyword) :: highlight rest
    | IdentifierP(identifier, rest) ->
        (identifier, Identifier) :: highlight rest
    | NumberP(number, rest) ->
        (number, Number) :: highlight rest
    | StringLiteralStart(quote, CharP '"' rest) ->
        (quote + "\"", String) :: highlight rest
    | StringLiteralStart(quote, []) -> // case where the string is not closed.
        (quote, String) :: []
    | NormalMatch(normalPart, rest) ->
        (normalPart, Normal) :: highlight rest
    | c :: rest -> // this should never happen, because NormalMatch will.
        printfn $"Warning: character '{c}' is not recognised by highlighter, default to Normal"
        (c.ToString(), Normal) :: highlight rest

//----------------------------------------------------------------------------------------------//
//----------------------------Mouse Event Handling & Cursor-------------------------------------//
//----------------------------------------------------------------------------------------------//

/// Handles editor mouse events.
/// evtype is the type of event (e.g., click, mousemove).
/// dispatch is the function to call to update the model.
/// ev is the mouse event from the browser.
// TODO: this should be improved to handle more mouse events: move, drag, up.
// NB maybe we should use a union type for the event type instead of a string?
let mouseEventHandler evType dispatch (ev: Browser.Types.MouseEvent) =
    let x = ev.clientX
    let y = ev.clientY
    let el = Browser.Dom.document.getElementById "codeEditorContainer"
    let sx = el.scrollLeft
    let sy = el.scrollTop
    let bb = el.getBoundingClientRect ()
    let x = (x + sx - bb.left - Constants.leftMargin) / 11. // 11 one char width
    let y = (y + sy - bb.top) / 30. // 30 is line height
    match evType with
    | "click" ->
        // SetCursor message calls updateEditorCursor
        dispatch
        <| CodeEditorMsg(SetCursor(max (int (x + 0.5)) 0, int y)) // offset by 0.5 chars
    | _ -> ()

/// Updates the Editor model with the new cursor position based on mouse coordinates.
/// xMouse and yMouse are the mouse coordinates.
/// xMouse specifies the column position in the line at which new chars are inserted
/// xMouse - 1 is the column that is deleted on backspace.
/// Model is the whole Issie Model - of which CodeEditorModel is one field.
let updateEditorCursor (xMouse: int) (yMouse: int) (model: Model) =
    let state =
        model.CodeEditorState
        |> Option.defaultValue initCodeEditorState
    let y = yMouse |> min (state.Lines.Length - 1) |> max 0
    let x = xMouse |> min (state.Lines[y].Length) |> max 0
    { model with
        CodeEditorState = Some { state with CursorPos = { X = x; Y = y } } }

//------------------------------------------------------------------------------------------//
//------------------------------Key Press Handling------------------------------------------//
//------------------------------------------------------------------------------------------//

/// Implements a model update for a key press event.
/// The keyCode is the code of the key pressed.
/// Normally, this is a character code, but it can also be a special key code (e.g., backspace).
/// Character codes are inserted. Special codes are handled differently.
/// Lines and CursorPos are updated accordingly.
let updateEditorOnKeyPress (keyPress: KeyPressInfo) (model: Model) =
    let state =
        model.CodeEditorState
        |> Option.defaultValue initCodeEditorState
    let key = keyPress.KeyString
    let modifiers =
        [
            if keyPress.ShiftKey then [ "Shift"] else []
            if keyPress.ControlKey then [ "Control"] else []
            if keyPress.AltKey then [ "Alt"] else []
            if keyPress.MetaKey then [ "Meta"] else []
        ] |> List.concat
    /// insert point is on the line indexed by cursorY
    let cursorY =
        int state.CursorPos.Y
        |> min (state.Lines.Length - 1)
        |> max 0
    // split up lines before doing processing
    let beforeLines, currentLine, afterLines =
        match state.Lines.Length with
        | 0 -> [], "", [] // if there are no lines make one empty line.
        | n ->
            List.splitAt (min cursorY n) state.Lines
            |> fun (before, after) -> before, after[0], after[1..]

    /// insert point is before the character at cursorX
    let cursorX = int state.CursorPos.X |> min currentLine.Length
    /// the output values here come from 5 separate items
    /// newLines is beforeLines, newLine, afterLines concatenated.
    /// The items all have default values.
    /// It would be better to use a record type to hold these values and update the fields
    /// of a default record value as needed. That would make the code clearer.
    let newCursorX, newCursorY, newLines =
        let insertIndex = min cursorX currentLine.Length
        let noChange = beforeLines @ [ currentLine ] @ afterLines
        match modifiers = [], key with
        | true, "Backspace" -> // backspace
            match currentLine with
            | _ when cursorX = 0 && cursorY = 0 -> cursorX, cursorY, noChange
            | line when cursorX = 0 ->
                beforeLines[cursorY - 1].Length,
                cursorY - 1,
                beforeLines[.. cursorY - 2] @ [ beforeLines[cursorY - 1] + line ] @ afterLines
            | line ->
                cursorX - 1,
                cursorY,
                beforeLines @ [ Seq.removeAt (cursorX - 1) line |> System.String.Concat ] @ afterLines
        | true, "Enter" -> // enter
            List.splitAt insertIndex (currentLine |> Seq.toList)
            |> fun (before, after) ->
                0,
                cursorY + 1,
                beforeLines @ [ before |> System.String.Concat; after |> System.String.Concat ] @ afterLines
        | _, key when key.Length = 1 && not (
                                                keyPress.AltKey
                                                || keyPress.ControlKey
                                                || keyPress.MetaKey
                                            ) ->
            cursorX + 1,
            cursorY,
            beforeLines @ [ currentLine.Insert(insertIndex, key) ] @ afterLines
        | _, key ->
            // printfn $"Key {key} with modifiers {modifiers} not handled"
            cursorX, cursorY, noChange
    // printfn $"Updating model with cursor = char:{newCursorX} line:{newCursorY}"
    let state =
        model.CodeEditorState
        |> Option.defaultValue initCodeEditorState
    { model with
        CodeEditorState = Some { state with
                                    Lines = newLines;
                                    CursorPos.X = newCursorX;
                                    CursorPos.Y = newCursorY } },
    Cmd.none

//------------------------------------------------------------------------------------//
//--------------------------------Editor Rendering------------------------------------//
//------------------------------------------------------------------------------------//

(*
    The editor is rendered as an HTML div.
    Each line of code is rendered as a single child div positioned absolute relative to the editor main div.
    Each line div comprises three overlaid divs each positioned absolute relative to the line div:

    1. The line text, highlighted by the highlighter.
    2. The line error indication, rendered in a separate div.
    3. The line cursor, rendered in a separate div.

    The line numbers are rendered in a separate div, scrolling with the main editor div.
    This allows the line numbers to scroll vertically with the editor.
    The line numbers must NOT appear to scroll horizontally with the main editor content.
    To implement this, the line numbers div is positioned absolutely, with a position that changes with the
    horizontal scroll position of the main editor div, so that the line number X position stays
    fixed at the left margin.
*)

/// Renders the line numbers in the left margin of the code editor.
/// xPosition is the X position of the left margin within the scrolling editor div
/// It changes to ensure the left margin stays fixed at the left of the editor.
/// The number of lines is determined by the number of lines in the model.
let renderLineNumbers xPosition (model: CodeEditorModel) =

    div
        [  Id "lineNumberColumn"
           Style
              [ CSSProp.Position PositionOptions.Absolute
                CSSProp.Left(xPosition)
                CSSProp.Width(Constants.leftMargin - Constants.leftGutter)
                CSSProp.Top 0
                CSSProp.LineHeight 1.5 // We use absoluite positioning for lines, so maybe this is not needed?
                                       // Removing it might alter the Y position of the line numbers
                CSSProp.OverflowY OverflowOptions.Visible
                CSSProp.OverflowX OverflowOptions.Visible
                CSSProp.ZIndex 10000 // make sure line numbers lie above the text
                BackgroundColor "#f0f0f0" // light grey background for left margin
        ] ] 
        ([ 1 .. max model.Lines.Length 1 ]
         |> List.map (fun i ->
             div
                 [  Id $"lineNumber-{i}"
                    Style
                       [ CSSProp.Position PositionOptions.Absolute
                         CSSProp.Left 0
                         CSSProp.Top(float (i - 1) * 30.) // 30 is line height
                         CSSProp.Width(Constants.leftMargin - Constants.leftGutter)
                         CSSProp.Height "30px" // line height. Maybe not needed?
                         CSSProp.TextAlign TextAlignOptions.Right
                         CSSProp.BackgroundColor "#f0f0f0" // light grey background for left margin
                         CSSProp.PaddingRight "5px" ] ]
                 [ str (sprintf "%d" i) ]))

/// Type of data  passed to the line render function from the main render function.
/// This is a subset of CodeEditorModel data and derived from this.
type LineData =
    {
        /// The line index (Y coord). Indices start from 0 and therefore are 1 less than the displayed line number.
        LineIndex: int
        /// the text of the line - before highlighting. Highlighting is done in the render function.
        LineText: string
        /// the X coordinate of the cursor in this line, in characters, or None if not on this line.
        CursorX: int option
        /// the errors for this line - a list of intervals. Currently all errors are shown.
        /// and the line render function filters for errors on the current line.
        Errors: Interval list
    }

/// Renders a single line of code in the editor, with optional cursor and error indication.
let renderOneEditorLine (data: LineData) : ReactElement =
    let { LineIndex = lineIndex; LineText = text; CursorX = cursorX; Errors = errors } = data
    // printfn $"Rendering line {lineIndex} cursor={cursorX}"
    /// the line error indications as a list of react elements
    /// each comprising a string of invisible characters that is highlighted
    /// or not.
    let errorLineReact =
        errors
        |> List.collect (
            intersectionOpt
                { Start = { X = 0; Y = lineIndex }
                  End = { X = float (text.Length + 1); Y = lineIndex } }
            >> Option.map (fun interval -> int interval.Start.X, int interval.End.X)
            >> Option.toList
        )
        |> List.sort
        |> function
            | [] -> []
            | lineErrors ->
                ((0, []), lineErrors)
                ||> List.fold (fun (start, segsSoFar) (errStart, errEnd) ->
                    let okSeg =
                        if errStart - 1 < start then
                            None
                        else
                            Some <| (false, errStart - start)
                    let errSeg =
                        if errStart > errEnd then
                            None
                        else
                            Some <| (true, errEnd - errStart)
                    errEnd + 1, List.concat [ Option.toList errSeg; Option.toList okSeg; segsSoFar ])
                |> (fun (_endIndex, segL) ->
                    segL
                    |> List.rev
                    |> List.mapi (fun i (isError, segLength) ->
                        /// space chars do not render underlines correctly
                        /// so we use a non-space with a Color = transparent instead to make an
                        /// underline overlay
                        let spaceChars = str <| String.replicate segLength "*"
                        if isError then
                            span
                                [ Id $"errorseg-{lineIndex}-{i}"
                                  Style
                                      [ TextDecoration "underline wavy red"
                                        // negative offset to make text above wavy line readable
                                        // requires enough space between lines (LineHeight =1.5 is fine)
                                        CSSProp.Custom("textUnderlineOffset", "15%")
                                        Position PositionOptions.Relative ] ]
                                [ spaceChars ]
                        else
                            span
                                [ Id $"okseg-{lineIndex}-{i}"; Style [ Position PositionOptions.Relative ] ]
                                [ spaceChars ])
                    |> (fun errorLineReactL ->
                        [ div
                              [ Id $"errorSegLine-{lineIndex}"
                                Style
                                    [ Position PositionOptions.Absolute // from CodeEditorLine
                                      CSSProp.Left 0
                                      CSSProp.Top 0
                                      WhiteSpace WhiteSpaceOptions.Pre
                                      VerticalAlign "middle"
                                      Background "transparent"
                                      Color "transparent"
                                      LineHeight 1.5 // we use absolute positioning for lines, so maybe this is not needed?
                                                     // removing it might alter the Y position of the text.
                                    ] ]
                              errorLineReactL ]))
    /// the line text elements as highlighted by highlighter
    let codeLineReact =
        let reactOfText =
            match text with
            | "" -> [ br [] ]
            | text ->
                highlight (text |> Seq.toList) // use highlighter to get highlighted text
                |> List.map (fun (text, elementType) ->
                    // better to implement this using a Map under Constants?
                    match elementType with
                    | Normal -> str text
                    | Keyword -> span [ Style [ Color "green" ] ] [ str text ]
                    | Identifier -> span [ Style [ Color "blue" ] ] [ str text ]
                    | Number -> span [ Style [ Color "orange" ] ] [ str text ]
                    | String -> span [ Style [ Color "red" ] ] [ str text ])
        [ span
              [ Id(sprintf "code-text-line-%d" lineIndex)
                Style
                    [ Position PositionOptions.Absolute // from codeEditorLine
                      VerticalAlign "middle"
                      CSSProp.Custom("scrollSnapAlign", "start")
                      LineHeight 1.5 // we use absolute positioning for lines, so maybe this is not needed?
                                     // removing it might alter the Y position of the text.
                      CSSProp.Left 0
                      CSSProp.Top 0
                      WhiteSpace WhiteSpaceOptions.Pre
                      Background "transparent" ] ]

              reactOfText ]
    /// the line cursor element - if needed
    let cursorReact =
        /// Renders the cursor in react at the given position as a single character padded to the left with a margin.
        /// The cursor is a vertical I-beam that blinks to indicate the current insert position.
        /// The returned react element has a ZIndex chosen so it lies above the text.
        let renderCursor (charIndex: int) =
            let xPosition = (float charIndex) * 11. - 9.5 // Small adjustment to make cursor before character
            div
                [ Id "cursor-div"
                  Style
                      [ Position PositionOptions.Absolute // from codeEditorLine
                        CSSProp.Left xPosition
                        CSSProp.Top 0
                        LineHeight 1.5 // This defines line height to be 1.5 X Font size.
                                       // We use absolute positioning for lines, so maybe this is not needed?
                                       // Removing it might alter the Y position of the cursor.
                        Background "transparent"
                        Animation "codeEditorBlink 1s steps(2, start) infinite" ] ] // Adjust blink duration and steps as needed
                [ str "\u2336" ] // Unicode character for I-beam cursor
        match cursorX with
        // cursor displays before the character at X = xPos
        | Some xPos -> [ renderCursor xPos ]
        | None -> []
    List.concat [ codeLineReact; errorLineReact; cursorReact ]
    |> div
        [ Id "codeEditorLine"
          Style
              [ // codeEditorLine is positioned absolute offset from codeEditorContainer
                Position PositionOptions.Absolute
                CSSProp.Left Constants.leftMargin
                CSSProp.Top(float lineIndex * 30.) // 30 is line height
                LineHeight 0 // the real LineHeight is defined in the inner div
                WhiteSpace WhiteSpaceOptions.Pre ] ]

/// Renders the complete code editor with syntax highlighting and error indication.
/// The editor is rendered in a div with a fixed size as specified in the CSS Height and Width props.
/// The height of the editor is rounded to fit an integral number of lines.
/// The editor is scrollable in both X and Y directions.
/// CoceEditorModel is the model for the code editor, and includes info
/// about the lines of code, the cursor position, and any errors.
let renderEditor (model: CodeEditorModel) (dispatch: Msg -> unit) =
    model.Lines
    |> List.mapi (fun lineIndex text ->
        let cursorX =
            if lineIndex = int model.CursorPos.Y then
                Some <| int model.CursorPos.X
            else
                None
        /// The input data extracted from the model for this line.
        /// TODO: Errors should be the list of intervals for this line only for greater efficiency
        /// but that will only be an issue if the error list is long.
        /// NB adding unnecessary data here will reduce performance.
        let lineData =
            { LineIndex = lineIndex
              LineText = text
              CursorX = cursorX
              Errors = model.Errors }

        // The next two lines are "magic" that implement React memoisation so that the function
        // renderOneEditorLine called below is not called unless the line data changes.
        // The editor will work (slower) with these two lines replaced by `|> renderOneEditorLine lineData`
        ($"codeEditorLine", Some(fun (props: LineData) -> $"{props.LineIndex}"), lineData) // magic
        |||> reactMemoize renderOneEditorLine) // magic
    //  |> renderOneEditorLine lineData // uncomment, and comment two above lines, to remove React memoisation
    |> (fun lines ->
        let scrollContainer = Browser.Dom.document.getElementById "codeEditorContainer"
        let hasXScroll =
            scrollContainer <> null
            && scrollContainer.scrollWidth > scrollContainer.clientWidth
        /// the vertical height taken up by a horizontal scrollbar
        let xScrollbarHeightPx =
            if scrollContainer = null || not hasXScroll then
                0.
            else
                scrollContainer.offsetHeight - scrollContainer.clientHeight
        let xScrollAmount =
            if scrollContainer = null then
                0.
            else
                scrollContainer.scrollLeft
        div
            // This is the main container for the code editor.
            // It contains all the editor content that scrolls.
            // It is sized to fit an integral number of editor lines
            [ Id "codeEditorContainer"
              OnClick(mouseEventHandler "click" dispatch) // used to position cursor on click
              OnMouseMove(mouseEventHandler "mousemove" dispatch) // not currently used
              OnScroll(fun ev ->
                  dispatch
                  <| CodeEditorMsg(EditorMsg.UpdateCodeEditorState id))

              Style
                  [ CSSProp.FontFamily "monospace" // don't change this - dimensions depend on it.
                    // for Monospace 30px, in dev tools, char size is 16.50 W X 35.05 H
                    // Lineheight 1.5 (used for inner divs and spans) sets Line height to 45
                    CSSProp.FontSize "20px" // scales all dimensions. Line height is 1.5 X this.
                    LineHeight "0" // real line height is defined in inner div
                    BorderTop "0px"
                    PaddingTop "0px"
                    PaddingBottom "0px"
                    BorderBottom "0px"
                    CSSProp.Position PositionOptions.Relative
                    CSSProp.OverflowY OverflowOptions.Auto
                    CSSProp.OverflowX OverflowOptions.Auto
                    CSSProp.ScrollSnapType "y mandatory" // snap to nearest line when vertical scrolling
                    // Work out correct height for integral number of lines.
                    // This is larger if the editor requires a horizontal scrollbar.
                    // 50vh is the approx desired window height. 45px is the line height.
                    // exact window height is varied to ensure editor holds exact number of lines
                    CSSProp.Height $"calc( round(50vh , 45px) + {xScrollbarHeightPx}px)"
                    // 50vw is the required editor width.
                    // NB - this may not be an exact number of chars.
                    // TODO - change this as per height to make it exact?
                    // This might cause oscillation due to interation between heights widths?
                    CSSProp.Width "50vw" ] ]
            (renderLineNumbers xScrollAmount model :: lines))

//---------------------------------------Top Level--------------------------------------------------//

/// Main editor model update function, called from Elmish main update when a CodeEditorMsg Message is received.
/// This allows additional Code Editor messages to be added here in the future without changing
/// the main update function. Note that the message type must still be changed in ModelType.fs since F#
/// does not support forward type references even to an opaque type.
/// NB - There is a way to get forward references - by parametrising the Model type - that is
/// theoretically neat, and possible, but in practice it is too messy to implement because type parameters
/// would then decorate the whole code base.
let update editorMsg model =
    let state =
        model.CodeEditorState
        |> Option.defaultValue initCodeEditorState
    match editorMsg with
    | SetCursor(xMouse, yMouse) -> updateEditorCursor xMouse yMouse model
    | UpdateCode updateFn ->
        { model with
            CodeEditorState = Some { state with Lines = updateFn state.Lines } }
    | SetErrors errors -> { model with CodeEditorState = Some { state with Errors = errors } }
    | UpdateCodeEditorState updateFn -> { model with CodeEditorState = Some(updateFn state) }
    |> fun model -> model, Cmd.none

/// Some ad hoc test data for the code editor used during development.
/// Change as needed, this is not a unit test.
let testEditorModel =

    let errorPosns =
        [ { Start = { X = 3; Y = 1 }; End = { X = 4; Y = 1 } }
          { Start = { X = 0; Y = 0 }; End = { X = 4; Y = 0 } }
          { Start = { X = 4; Y = 10 }; End = { X = 6; Y = 11 } } ]
    let numberOfLines = 200
    let initialCodeLines =
        [ "   if then else module 123 yellow Big!"; "a  test 5 "; "     ;   ;" ]

    let replicatedLine = "test2 bbbb 1234 () \"abcdef\" 6754 (Ta123) "
    { Lines =
        initialCodeLines
        @ List.replicate (numberOfLines - 3) replicatedLine
      Errors = errorPosns
      CursorPos = { X = 10; Y = 0 } }
