﻿module System.Text.Json.Tests.FSharp.EnumTests

open System
open System.Reflection
open System.Text.Json
open System.Text.Json.Serialization
open Xunit

[<Flags>]
type BadEnum =
  | ``There's a comma, in my name`` = 1
  | ``There's a comma, even here`` = 2
  | ``ThisisagoodEnumValue`` = 4

let badEnum = BadEnum.``There's a comma, in my name`` ||| BadEnum.``There's a comma, even here``
let badEnumJsonStr = $"\"{badEnum}\""

type NumericLabelEnum =
  | ``1`` = 1
  | ``2`` = 2
  | ``3`` = 4
  
let enumWithNumericValue = NumericLabelEnum.``1``
let enumWithNumericValueJsonStr = $"\"{enumWithNumericValue}\""

let enumWithNonMatchedUnderlyingNumericValue = NumericLabelEnum.``3``
let enumWithNonMatchedUnderlyingNumericValueJsonStr = $"\"{enumWithNonMatchedUnderlyingNumericValue}\""

let badEnumWithGoodValue = BadEnum.ThisisagoodEnumValue
let badEnumWithGoodValueJsonStr = $"\"{badEnumWithGoodValue}\""

[<Flags>]
type GoodEnum =
  | Thereisnocommainmyname_1 = 1
  | Thereisnocommaevenhere_2 = 2

let goodEnum = GoodEnum.Thereisnocommainmyname_1 ||| GoodEnum.Thereisnocommaevenhere_2
let goodEnumJsonStr = $"\"{goodEnum}\""

let options = new JsonSerializerOptions()
options.Converters.Add(new JsonStringEnumConverter())

let optionsDisableNumeric = new JsonSerializerOptions()
optionsDisableNumeric.Converters.Add(new JsonStringEnumConverter(null, false))

[<Fact>]
let ``Deserialize With Exception If Enum Contains Special Char`` () =
    let ex = Assert.Throws<TargetInvocationException>(fun () -> JsonSerializer.Deserialize<BadEnum>(badEnumJsonStr, options) |> ignore)
    Assert.Equal(typeof<InvalidOperationException>, ex.InnerException.GetType())
    Assert.Equal("Enum type 'BadEnum' uses unsupported identifer name 'There's a comma, in my name'.", ex.InnerException.Message)


[<Fact>]
let ``Serialize With Exception If Enum Contains Special Char`` () =
    let ex = Assert.Throws<TargetInvocationException>(fun () ->  JsonSerializer.Serialize(badEnum, options) |> ignore)
    Assert.Equal(typeof<InvalidOperationException>, ex.InnerException.GetType())
    Assert.Equal("Enum type 'BadEnum' uses unsupported identifer name 'There's a comma, in my name'.", ex.InnerException.Message)

[<Fact>]
let ``Successful Deserialize Normal Enum`` () =
    let actual = JsonSerializer.Deserialize<GoodEnum>(goodEnumJsonStr, options)
    Assert.Equal(GoodEnum.Thereisnocommainmyname_1 ||| GoodEnum.Thereisnocommaevenhere_2, actual)

[<Fact>]
let ``Fail Deserialize Good Value Of Bad Enum Type`` () =
    let ex = Assert.Throws<TargetInvocationException>(fun () -> JsonSerializer.Deserialize<BadEnum>(badEnumWithGoodValueJsonStr, options) |> ignore)
    Assert.Equal(typeof<InvalidOperationException>, ex.InnerException.GetType())
    Assert.Equal("Enum type 'BadEnum' uses unsupported identifer name 'There's a comma, in my name'.", ex.InnerException.Message)

[<Fact>]
let ``Fail Serialize Good Value Of Bad Enum Type`` () =
    let ex = Assert.Throws<TargetInvocationException>(fun () ->  JsonSerializer.Serialize(badEnumWithGoodValue, options) |> ignore)
    Assert.Equal(typeof<InvalidOperationException>, ex.InnerException.GetType())
    Assert.Equal("Enum type 'BadEnum' uses unsupported identifer name 'There's a comma, in my name'.", ex.InnerException.Message)

[<Fact>]
let ``Fail Deserialize Numeric label Of Enum When Disallow Integer Values`` () =
    Assert.Throws<JsonException>(fun () -> JsonSerializer.Deserialize<NumericLabelEnum>(enumWithNumericValueJsonStr, optionsDisableNumeric) |> ignore)
    
[<Fact>]
let ``Successful Deserialize Numeric label Of Enum When Allowing Integer Values`` () =
    let actual = JsonSerializer.Deserialize<NumericLabelEnum>(enumWithNumericValueJsonStr, options)
    Assert.Equal(NumericLabelEnum.``1``, actual)
    
[<Fact>]
let ``Successful Deserialize Numeric label Of Enum But as Underlying value When Allowing Integer Values`` () =
    let actual = JsonSerializer.Deserialize<NumericLabelEnum>(enumWithNonMatchedUnderlyingNumericValueJsonStr, options)
    Assert.NotEqual(LanguagePrimitives.EnumOfValue 4, actual)