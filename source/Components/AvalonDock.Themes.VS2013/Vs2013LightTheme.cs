/************************************************************************
   AvalonDock

   Copyright (C) 2007-2013 Xceed Software Inc.

   This program is provided to you under the terms of the Microsoft Public
   License (Ms-PL) as published at https://opensource.org/licenses/MS-PL
 ************************************************************************/

namespace AvalonDock.Themes
{
	/// <inheritdoc/>
	public class Vs2013LightTheme : DictionaryTheme
	{
		/// <inheritdoc/>
		public Vs2013LightTheme()
			: base(VsThemePaletteFactory.BuildDictionary(VsThemeResources.Light)) { }
	}
}