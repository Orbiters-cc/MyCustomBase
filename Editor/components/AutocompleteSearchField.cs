using System;
using System.Collections.Generic;
using UnityEngine;

#if UNITY_EDITOR
using UnityEditor;
using UnityEditor.IMGUI.Controls;

namespace AutocompleteSearchField
{
	[Serializable]
	public class AutocompleteSearchField
	{
 	static class Styles
 	{
 		public const float resultHeight = 20f;
 		public const float resultsBorderWidth = 2f;
 		public const float resultsMargin = 15f;
 		public const float resultsLabelOffset = 2f;
 		public const float maxResultsHeight = 150f;

 		public static readonly GUIStyle entryEven;
 		public static readonly GUIStyle entryOdd;
 		public static readonly GUIStyle labelStyle;
 		public static readonly GUIStyle resultsBorderStyle;

 		static Styles()
 		{
 			entryOdd = new GUIStyle("CN EntryBackOdd");
 			entryEven = new GUIStyle("CN EntryBackEven");
 			resultsBorderStyle = new GUIStyle("hostview");

 			labelStyle = new GUIStyle(EditorStyles.label)
 			{
 				alignment = TextAnchor.MiddleLeft,
 				richText = true
 			};
 		}
 	}

 	public Action<string> onInputChanged;
 	public Action<string> onConfirm;
 	public string searchString;
 	public int maxResults = 15;

 	[SerializeField]
 	List<string> results = new List<string>();

 	[SerializeField]
 	int selectedIndex = -1;

 	SearchField searchField;

 	Vector2 previousMousePosition;
 	bool selectedIndexByMouse;

 	bool showResults;
 	Vector2 scrollPosition;

		public void AddResult(string result)
		{
			results.Add(result);
		}

		public void ClearResults()
		{
			results.Clear();
		}

		public void OnToolbarGUI()
		{
			Draw(asToolbar:true);
		}

		public void OnGUI()
		{
			Draw(asToolbar:false);
		}

 	void Draw(bool asToolbar)
 	{
 		var rect = GUILayoutUtility.GetRect(1, 1, 18, 18, GUILayout.ExpandWidth(true));
 		GUILayout.BeginHorizontal();
 		DoSearchField(rect, asToolbar);
 		GUILayout.EndHorizontal();
		
 		// Reserve space for results if they should be shown
 		if (results.Count > 0 && showResults)
 		{
 			// Calculate total height needed for all results
 			float totalResultsHeight = Styles.resultHeight * results.Count;
 			// Cap at maximum height
 			float displayHeight = Mathf.Min(totalResultsHeight, Styles.maxResultsHeight);
 			// Add border width
 			displayHeight += Styles.resultsBorderWidth;
			
 			// Reserve the space in the layout
 			Rect resultsRect = GUILayoutUtility.GetRect(1, displayHeight, GUILayout.ExpandWidth(true));
 			DoResults(resultsRect, totalResultsHeight);
 		}
 	}

		void DoSearchField(Rect rect, bool asToolbar)
		{
			if(searchField == null)
			{
				searchField = new SearchField();
				searchField.downOrUpArrowKeyPressed += OnDownOrUpArrowKeyPressed;
			}

			var result = asToolbar
				? searchField.OnToolbarGUI(rect, searchString)
				: searchField.OnGUI(rect, searchString);

			if (result != searchString && onInputChanged != null)
			{
				onInputChanged(result);
				selectedIndex = -1;
				showResults = true;
			}

			searchString = result;

			if(HasSearchbarFocused())
			{
				RepaintFocusedWindow();
			}
		}

		void OnDownOrUpArrowKeyPressed()
		{
			var current = Event.current;

			if (current.keyCode == KeyCode.UpArrow)
			{
				current.Use();
				selectedIndex--;
				selectedIndexByMouse = false;
			}
			else
			{
				current.Use();
				selectedIndex++;
				selectedIndexByMouse = false;
			}

			if (selectedIndex >= results.Count) selectedIndex = results.Count - 1;
			else if (selectedIndex < 0) selectedIndex = -1;
		}

 	void DoResults(Rect rect, float totalResultsHeight)
 	{
 		if(results.Count <= 0 || !showResults) return;

 		var current = Event.current;
		
 		// Adjust rect for margins
 		rect.x += Styles.resultsMargin;
 		rect.width -= Styles.resultsMargin * 2;
		
 		// Draw border around the results area
 		GUI.Label(rect, "", Styles.resultsBorderStyle);
		
 		var mouseIsInResultsRect = rect.Contains(current.mousePosition);
		
 		if(mouseIsInResultsRect)
 		{
 			RepaintFocusedWindow();
 		}
		
 		var movedMouseInRect = previousMousePosition != current.mousePosition;
		
 		// Create inner rect for content (inside the border)
 		Rect innerRect = new Rect(
 			rect.x + Styles.resultsBorderWidth, 
 			rect.y + Styles.resultsBorderWidth, 
 			rect.width - Styles.resultsBorderWidth * 2, 
 			rect.height - Styles.resultsBorderWidth * 2
 		);
		
  	// Determine if we need scrolling
  	bool needsScrolling = totalResultsHeight > Styles.maxResultsHeight;
	
  	// Begin scroll view if needed
  	if (needsScrolling)
  	{
  		Rect scrollViewRect = innerRect;
  		Rect contentRect = new Rect(0, 0, innerRect.width - 20, totalResultsHeight); // -20 for scrollbar
  		scrollPosition = GUI.BeginScrollView(scrollViewRect, scrollPosition, contentRect);
  	}
	
  	// Draw each result
  	// When scrolling, use local coordinates (0, 0) - ScrollView handles transformation
  	// When not scrolling, use innerRect coordinates so drawing appears in correct position
  	float startX = needsScrolling ? 0 : innerRect.x;
  	float startY = needsScrolling ? 0 : innerRect.y;
  	Rect elementRect = new Rect(startX, startY, innerRect.width - (needsScrolling ? 20 : 0), Styles.resultHeight);
  	var didJustSelectIndex = false;
	
  	for (var i = 0; i < results.Count; i++)
  	{
  		// Calculate absolute position for mouse detection
  		Rect absoluteElementRect;
  		if (needsScrolling)
  		{
  			// Inside ScrollView: convert from scroll space to screen space
  			absoluteElementRect = new Rect(
  				innerRect.x + elementRect.x + scrollPosition.x,
  				innerRect.y + elementRect.y - scrollPosition.y,
  				elementRect.width,
  				elementRect.height
  			);
  		}
  		else
  		{
  			// Not scrolling: elementRect is already in screen space
  			absoluteElementRect = elementRect;
  		}
		
  		if(current.type == EventType.Repaint)
  		{
  			var style = i % 2 == 0 ? Styles.entryOdd : Styles.entryEven;
  			style.Draw(elementRect, false, false, i == selectedIndex, false);
			
  			var labelRect = elementRect;
  			labelRect.x += Styles.resultsLabelOffset;
  			GUI.Label(labelRect, results[i], Styles.labelStyle);
  		}
		
  		if(absoluteElementRect.Contains(current.mousePosition))
  		{
  			if(movedMouseInRect)
  			{
  				selectedIndex = i;
  				selectedIndexByMouse = true;
  				didJustSelectIndex = true;
  			}
  			if(current.type == EventType.MouseDown)
  			{
  				OnConfirm(results[i]);
  			}
  		}
		
  		elementRect.y += Styles.resultHeight;
  	}
		
 		if (needsScrolling)
 		{
 			GUI.EndScrollView();
 		}
		
 		if(current.type == EventType.Repaint && !didJustSelectIndex && !mouseIsInResultsRect && selectedIndexByMouse)
 		{
 			selectedIndex = -1;
 		}
		
 		if((GUIUtility.hotControl != searchField.searchFieldControlID && GUIUtility.hotControl > 0)
 			|| (current.rawType == EventType.MouseDown && !mouseIsInResultsRect))
 		{
 			showResults = false;
 		}
		
 		if(current.type == EventType.KeyUp && current.keyCode == KeyCode.Return && selectedIndex >= 0)
 		{
 			OnConfirm(results[selectedIndex]);
 		}
		
 		if(current.type == EventType.Repaint)
 		{
 			previousMousePosition = current.mousePosition;
 		}
 	}

		void OnConfirm(string result)
		{
			searchString = result;
			if(onConfirm != null) onConfirm(result);
			if(onInputChanged != null) onInputChanged(result);
			RepaintFocusedWindow();
			GUIUtility.keyboardControl = 0; // To avoid Unity sometimes not updating the search field text
		}

		bool HasSearchbarFocused()
		{
			return GUIUtility.keyboardControl == searchField.searchFieldControlID;
		}

		static void RepaintFocusedWindow()
		{
			if(EditorWindow.focusedWindow != null)
			{
				EditorWindow.focusedWindow.Repaint();
			}
		}
	}
}
#endif