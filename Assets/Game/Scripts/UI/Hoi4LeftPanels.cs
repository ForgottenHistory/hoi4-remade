namespace Hoi4.UI
{
    /// <summary>
    /// GAME: the screens that share the left side under the top bar (research with its tech tree, production,
    /// construction, deployment). A plain map click (one no screen took for itself: placing a building, picking a deploy
    /// location) closes them all, then opens the state view (left click) or diplomacy (right click), like HOI4.
    /// </summary>
    public static class Hoi4LeftPanels
    {
        public static void CloseAll()
        {
            if (Hoi4ResearchPanel.IsOpen) Hoi4ResearchPanel.Instance.Close();      // and its tech tree
            if (Hoi4ProductionPanel.IsOpen) Hoi4ProductionPanel.Instance.Toggle();
            if (Hoi4ConstructionPanel.IsOpen) Hoi4ConstructionPanel.Instance.Close();
            if (Hoi4DeploymentPanel.IsOpen) Hoi4DeploymentPanel.Instance.Close();
        }
    }
}
